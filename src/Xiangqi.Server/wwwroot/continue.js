// 断点续弈 (continue from a position) — replay, breakpoint, 续弈服务费 via KasWare or manual fallback.
(function () {
  "use strict";
  const $ = function (id) { return document.getElementById(id); };
  let cfg = null;
  let replay = null;      // { fens, iccs, chinese, startFen }
  let ply = 0;
  let cs = null;          // continue session dto (+ token)
  let pollTimer = null;
  let pendingTx = null;   // last broadcast txid awaiting confirmation
  const STORE = "xiangqi-continue";
  const HEX64 = /^[0-9a-fA-F]{64}$/;
  const TOO_MANY = "请求过于频繁，请稍后再试 Too many requests, slow down";

  async function postJsonEx(url, body) {
    const response = await fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body)
    });
    const raw = await response.text();
    let data = null;
    if (raw) {
      try { data = JSON.parse(raw); }
      catch { data = { error: raw }; }
    }
    const header = response.headers.get("Retry-After");
    const parsed = header == null || header === "" ? NaN : Number(header);
    return { ok: response.ok, status: response.status, data: data, retryAfter: Number.isFinite(parsed) ? parsed : null };
  }

  function fenRows(fen) {
    const rows = fen.split(" ")[0].split("/");
    return rows.map(function (r) {
      const out = [];
      for (const ch of r) {
        if (ch >= "1" && ch <= "9") for (let i = 0; i < Number(ch); i++) out.push("");
        else out.push(ch);
      }
      return out;
    });
  }

  function info(text, kind) {
    const node = $("contInfo");
    node.textContent = text;
    node.className = "message " + (kind || "");
  }

  function showPly() {
    if (!replay) return;
    const fen = replay.fens[ply];
    session = { roomId: null, token: null, color: null, spectating: true, cont: false };
    stopPoll();
    preview = true;
    selected = null;
    state = { id: null, board: fenRows(fen), sideToMove: fen.split(" ")[1] === "b" ? "black" : "red", status: "ongoing",
      inCheck: false, lastMove: ply > 0 ? replay.iccs[ply - 1] : null, history: [], seats: null, fen: fen };
    render();
    $("plySlider").max = String(replay.fens.length - 1);
    $("plySlider").value = String(ply);
    $("plyLabel").textContent = "第 " + ply + " / " + (replay.fens.length - 1) + " 步" +
      (ply > 0 ? " · " + replay.chinese[ply - 1] + " (" + replay.iccs[ply - 1] + ")" : " · 起始局面 Start");
    $("contColor").value = state.sideToMove;
  }

  async function load(event) {
    event.preventDefault();
    const res = await postJson("/api/continue/parse", { fen: $("contFen").value.trim() || null, moves: $("contMoves").value.trim() || null });
    if (!res.ok) { info((res.data && res.data.error) || "无法解析棋谱。 Could not parse.", "error"); return; }
    replay = res.data;
    ply = replay.fens.length - 1;
    $("replayNav").hidden = false;
    info("已载入 " + replay.iccs.length + " 步。选择断点后点击「从此续下」。 Pick a breakpoint.", "");
    showPly();
  }

  function step(delta) {
    if (!replay) return;
    ply = Math.max(0, Math.min(replay.fens.length - 1, ply + delta));
    showPly();
  }

  function save() {
    if (cs) localStorage.setItem(STORE, JSON.stringify({ id: cs.id, token: cs.token, txid: pendingTx }));
  }

  function setPendingTx(txid) {
    pendingTx = txid == null ? null : String(txid);
    save();
  }

  function clearPendingTx() {
    pendingTx = null;
    save();
  }

  async function adopt(dto, token) {
    cs = Object.assign({}, dto, { token: token || (cs && cs.token) });
    if (cs.status === "active") pendingTx = null;
    save();
    session = { roomId: null, token: cs.token, color: cs.humanColor, spectating: false, cont: true,
      contStatus: cs.status, difficulty: cs.difficulty };
    const st = Object.assign({ id: "cont-" + cs.id, seats: null, name: "断点续弈" }, cs.state);
    preview = false;
    seenPly = -1;
    await applyState(st);
    paintPay();
  }

  async function create() {
    if (!replay) { info("请先载入棋谱或 FEN。 Load a record first.", "error"); return; }
    const res = await postJsonEx("/api/continue/sessions", {
      fen: replay.startFen, moves: replay.iccs.join(" "), ply: ply,
      difficulty: $("contDifficulty").value, humanColor: $("contColor").value
    });
    if (res.status === 429) { info((res.data && res.data.error) || TOO_MANY, "error"); return; }
    if (!res.ok) { info((res.data && res.data.error) || "创建失败。", "error"); return; }
    await adopt(res.data, res.data.token);
    info("续弈会话已创建，请签名并支付续弈服务费（1 KAS）。", "");
  }

  function paintPay() {
    const box = $("payBox");
    box.hidden = !cs || cs.status === "active" || cs.status === "finished";
    if (!cs) return;
    $("payMessage").textContent = cs.message;
    $("payTo").textContent = cs.payToAddress || "未配置收款地址";
    $("payStatus").textContent = ({ pending_auth: "① 待签名 Sign", pending_payment: "② 待支付 Pay", active: "已激活 Active", finished: "已结束 Finished", expired: "已过期 Expired" })[cs.status] || cs.status;
    $("authFields").hidden = cs.status !== "pending_auth";
    const expiredPayable = cs.status === "expired" && cs.address;
    $("txFields").hidden = cs.status !== "pending_payment" && !expiredPayable;
    $("kasBtn").textContent = cs.status === "pending_auth" ? "KasWare 连接并签名" : "KasWare 支付 1 KAS";
  }

  async function auth(address, signature) {
    const res = await postJsonEx("/api/continue/sessions/" + cs.id + "/auth",
      { token: cs.token, address: address, signature: signature, timestamp: cs.timestamp });
    if (res.status === 429) { info((res.data && res.data.error) || TOO_MANY, "error"); return false; }
    if (!res.ok) { info((res.data && res.data.error) || "签名验证失败。", "error"); return false; }
    await adopt(res.data);
    info("签名已验证，地址 " + address + "。请支付 1 KAS 续弈服务费。", "");
    return true;
  }

  function retryPay(txid, delayMs) {
    clearTimeout(pollTimer);
    pollTimer = setTimeout(function () { void pay(txid); }, delayMs == null ? 5000 : delayMs);
  }

  async function pay(txid) {
    setPendingTx(txid);
    let res;
    try {
      res = await postJsonEx("/api/continue/sessions/" + cs.id + "/payment", { token: cs.token, txid: txid });
    } catch (err) {
      info("网络异常，5 秒后重试支付确认… Network error, retrying payment check…", "");
      retryPay(txid);
      return;
    }
    if (res.status === 429) {
      const wait = Math.max(5, Number.isFinite(res.retryAfter) ? res.retryAfter : 0);
      info((res.data && res.data.error) || TOO_MANY, "error");
      retryPay(txid, wait * 1000);
      return;
    }
    if (res.status === 202 || res.status >= 500) {
      info((res.data && res.data.info) || "交易确认中，稍后自动重试… Waiting for confirmations…", "");
      retryPay(txid);
      return;
    }
    if (res.ok) {
      clearPendingTx();
      await adopt(res.data);
      info("续弈服务费已确认，开始对局！ Paid — your move.", "");
      return;
    }
    if (res.status === 410) {
      info("会话已过期。如果您已经转账，请保留此交易 ID 并联系客服恢复：" + txid + " ／ Session expired. If you already paid, keep this txid and contact support: " + txid, "error");
      return;
    }
    if (res.status === 400 || res.status === 403 || res.status === 404 || res.status === 409 || res.status === 413) {
      clearPendingTx();
    }
    info((res.data && res.data.error) || "支付验证失败。", "error");
  }

  async function kasware() {
    const w = window.kasware;
    if (!w) { info("未检测到 KasWare 钱包，请使用下方手动方式。 KasWare not found — use manual fields.", "error"); return; }
    try {
      const accounts = await w.requestAccounts();
      const address = accounts && accounts[0];
      if (cs.status === "pending_auth") {
        const sig = await w.signMessage(cs.message, { type: "schnorr" });
        await auth(address, typeof sig === "string" ? sig : (sig && sig.signature));
      } else if (cs.status === "pending_payment") {
        if (!cs.payToAddress) { info("未配置收款地址。", "error"); return; }
        const tx = await w.sendKaspa(cs.payToAddress, cs.amountSompi);
        let txid = tx;
        if (typeof tx === "string" && tx.trim().startsWith("{")) { try { const o = JSON.parse(tx); txid = o.id || o.txid || o.transactionId; } catch { } }
        else if (tx && typeof tx === "object") txid = tx.id || tx.txid || tx.transactionId;
        $("manTx").value = txid || "";
        setPendingTx(String(txid).trim());
        await pay(String(txid).trim());
      }
    } catch (err) {
      info("钱包操作失败: " + (err && err.message ? err.message : err), "error");
    }
  }

  async function play(notation) {
    if (busy || !cs) return;
    busy = true;
    selected = null;
    render();
    try {
      const res = await postJson("/api/continue/sessions/" + cs.id + "/moves", { token: cs.token, move: notation });
      if (!res.ok) { setNotice((res.data && res.data.error) || "走法被拒绝。", "error"); render(); return; }
      await adopt(res.data);
    } finally { busy = false; }
  }

  async function restore() {
    let saved = null;
    try { saved = JSON.parse(localStorage.getItem(STORE) || "null"); } catch { saved = null; }
    if (!saved || !saved.id) return;
    const r = await fetch("/api/continue/sessions/" + saved.id);
    if (r.status === 404) { localStorage.removeItem(STORE); return; }
    if (!r.ok) return;
    const dto = await r.json();
    if (dto.status === "finished" || dto.state.status !== "ongoing") return;
    if (typeof saved.txid === "string" && HEX64.test(saved.txid) && dto.status === "pending_payment") {
      pendingTx = saved.txid;
      cs = { token: saved.token };
      info("检测到未确认的支付，正在自动重试… Resuming payment check…", "");
      await adopt(dto, saved.token);
      void pay(saved.txid);
      return;
    }
    $("contResume").hidden = false;
    $("contResume").onclick = function () {
      pendingTx = saved.txid || null;
      cs = { token: saved.token };
      void adopt(dto, saved.token);
      $("contResume").hidden = true;
    };
  }

  async function init() {
    window.XqContinue = { play: play };
    const r = await fetch("/api/continue/config");
    cfg = r.ok ? await r.json() : { enabled: false };
    $("contFee").textContent = cfg.enabled
      ? "续弈服务费 " + cfg.amountKas + " KAS / 局 · 收款地址 " + cfg.payToAddress
      : "断点续弈付费服务未开放：未配置收款地址。 Paid continue is not open (no pay-to address).";
    $("contCreate").disabled = !cfg.enabled;
    $("contLoad").addEventListener("submit", function (e) { void load(e); });
    $("plyPrev").onclick = function () { step(-1); };
    $("plyNext").onclick = function () { step(1); };
    $("plyFirst").onclick = function () { step(-9999); };
    $("plyLast").onclick = function () { step(9999); };
    $("plySlider").oninput = function () { ply = Number($("plySlider").value); showPly(); };
    $("contCreate").onclick = function () { void create(); };
    $("kasBtn").onclick = function () { void kasware(); };
    $("manAuth").onclick = function () { void auth($("manAddr").value.trim(), $("manSig").value.trim()); };
    $("manPay").onclick = function () { void pay($("manTx").value.trim()); };
    $("copyMsg").onclick = function () { void navigator.clipboard.writeText(cs ? cs.message : ""); };
    try { await restore(); } catch (e) { console.warn(e); }
  }

  void init();
})();
