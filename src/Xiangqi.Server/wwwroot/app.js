"use strict";

const GLYPH = {
  K: "帥", A: "仕", B: "相", N: "傌", R: "俥", C: "炮", P: "兵",
  k: "將", a: "士", b: "象", n: "馬", r: "車", c: "砲", p: "卒"
};

const START_ROWS = [
  ["r", "n", "b", "a", "k", "a", "b", "n", "r"],
  ["", "", "", "", "", "", "", "", ""],
  ["", "c", "", "", "", "", "", "c", ""],
  ["p", "", "p", "", "p", "", "p", "", "p"],
  ["", "", "", "", "", "", "", "", ""],
  ["", "", "", "", "", "", "", "", ""],
  ["P", "", "P", "", "P", "", "P", "", "P"],
  ["", "C", "", "", "", "", "", "C", ""],
  ["", "", "", "", "", "", "", "", ""],
  ["R", "N", "B", "A", "K", "A", "B", "N", "R"]
];

const CELL = 52;
const LEFT = 48;
const TOP = 28;
const RIGHT = 28;
const BOTTOM = 46;
const WIDTH = LEFT + CELL * 8 + RIGHT;
const HEIGHT = TOP + CELL * 9 + BOTTOM;
const NS = "http://www.w3.org/2000/svg";
const SESSION_KEY = "xiangqi-session";

let state = {
  id: null,
  name: "",
  board: START_ROWS,
  sideToMove: "red",
  status: "ongoing",
  inCheck: false,
  lastMove: null,
  history: [],
  seats: null,
  fen: ""
};
let preview = true;
let session = { roomId: null, token: null, color: null, spectating: false };
let selected = null;
let seenPly = -1;
let legalMoves = [];
let legalTicket = 0;
let notice = null;
let busy = false;
let connection = null;
let joinedRoom = null;
let pollTimer = null;
let boardReady = false;
let highlightLayer = null;
let pieceLayer = null;

function xOf(file) { return LEFT + file * CELL; }
function yOf(rank) { return TOP + (9 - rank) * CELL; }

function el(name, attrs) {
  const node = document.createElementNS(NS, name);
  if (attrs) {
    for (const key of Object.keys(attrs))
      node.setAttribute(key, String(attrs[key]));
  }
  return node;
}

function textNode(content, x, y, className) {
  const node = el("text", { x: x, y: y, class: className });
  node.textContent = content;
  return node;
}

function ensureBoard() {
  if (boardReady) return;
  boardReady = true;
  const svg = document.getElementById("board");
  svg.setAttribute("viewBox", "0 0 " + WIDTH + " " + HEIGHT);
  const title = el("title");
  title.textContent = "象棋棋盘 Xiangqi board";
  svg.appendChild(title);
  svg.appendChild(buildDefs());
  svg.appendChild(buildStatic());
  highlightLayer = el("g");
  pieceLayer = el("g");
  const hits = el("g");
  svg.appendChild(highlightLayer);
  svg.appendChild(pieceLayer);
  svg.appendChild(hits);
  for (let rank = 0; rank < 10; rank++) {
    for (let file = 0; file < 9; file++) {
      const hit = el("circle", {
        cx: xOf(file),
        cy: yOf(rank),
        r: 22,
        class: "hit",
        "data-file": file,
        "data-rank": rank
      });
      hits.appendChild(hit);
    }
  }
  svg.addEventListener("click", onBoardClick);
}

function buildDefs() {
  const defs = el("defs");
  const grad = el("radialGradient", { id: "discGrad", cx: "35%", cy: "32%", r: "70%" });
  grad.appendChild(el("stop", { offset: "0%", "stop-color": "#fff8ea" }));
  grad.appendChild(el("stop", { offset: "100%", "stop-color": "#e6d0a4" }));
  defs.appendChild(grad);
  return defs;
}

function buildStatic() {
  const g = el("g");
  g.appendChild(el("rect", { x: 0, y: 0, width: WIDTH, height: HEIGHT, class: "board-bg" }));
  const top = yOf(9);
  const bottom = yOf(0);
  const riverTop = yOf(5);
  const riverBottom = yOf(4);
  g.appendChild(el("rect", {
    x: xOf(0),
    y: riverTop,
    width: CELL * 8,
    height: riverBottom - riverTop,
    class: "river"
  }));
  for (let file = 0; file < 9; file++)
    g.appendChild(el("line", { x1: xOf(file), y1: top, x2: xOf(file), y2: bottom, class: "grid-line" }));
  for (let rank = 0; rank < 10; rank++)
    g.appendChild(el("line", { x1: xOf(0), y1: yOf(rank), x2: xOf(8), y2: yOf(rank), class: "grid-line" }));
  g.appendChild(el("line", { x1: xOf(3), y1: yOf(0), x2: xOf(5), y2: yOf(2), class: "grid-line" }));
  g.appendChild(el("line", { x1: xOf(5), y1: yOf(0), x2: xOf(3), y2: yOf(2), class: "grid-line" }));
  g.appendChild(el("line", { x1: xOf(3), y1: yOf(9), x2: xOf(5), y2: yOf(7), class: "grid-line" }));
  g.appendChild(el("line", { x1: xOf(5), y1: yOf(9), x2: xOf(3), y2: yOf(7), class: "grid-line" }));
  const stars = [[1, 2], [7, 2], [1, 7], [7, 7]];
  for (const file of [0, 2, 4, 6, 8]) {
    stars.push([file, 3]);
    stars.push([file, 6]);
  }
  for (const spot of stars)
    drawStar(g, spot[0], spot[1]);
  const midY = (yOf(5) + yOf(4)) / 2;
  g.appendChild(textNode("楚河", (xOf(0) + xOf(4)) / 2, midY, "river-text"));
  g.appendChild(textNode("汉界", (xOf(4) + xOf(8)) / 2, midY, "river-text"));
  for (let file = 0; file < 9; file++)
    g.appendChild(textNode(String.fromCharCode(97 + file), xOf(file), yOf(0) + 34, "coord"));
  for (let rank = 0; rank < 10; rank++)
    g.appendChild(textNode(String(rank), xOf(0) - 32, yOf(rank), "coord"));
  g.appendChild(el("rect", {
    x: xOf(0) - 7,
    y: yOf(9) - 7,
    width: CELL * 8 + 14,
    height: CELL * 9 + 14,
    class: "frame"
  }));
  return g;
}

function drawStar(parent, file, rank) {
  const x = xOf(file);
  const y = yOf(rank);
  const gap = 4;
  const len = 8;
  const sides = [];
  if (file > 0) sides.push(-1);
  if (file < 8) sides.push(1);
  for (const sx of sides) {
    for (const sy of [-1, 1]) {
      const x0 = x + sx * gap;
      const y0 = y + sy * gap;
      parent.appendChild(el("line", {
        x1: x0, y1: y0, x2: x + sx * (gap + len), y2: y0, class: "star"
      }));
      parent.appendChild(el("line", {
        x1: x0, y1: y0, x2: x0, y2: y + sy * (gap + len), class: "star"
      }));
    }
  }
}

function pieceAt(file, rank) {
  const row = state.board && state.board[9 - rank];
  if (!row) return "";
  return row[file] || "";
}

function squareName(file, rank) {
  return String.fromCharCode(97 + file) + String(rank);
}

function parseSquare(text) {
  const value = text.toLowerCase();
  return { file: value.charCodeAt(0) - 97, rank: value.charCodeAt(1) - 48 };
}

function canMove() {
  return !preview && !!session.token && state.status === "ongoing" && state.sideToMove === session.color;
}

function isOwn(piece) {
  const redPiece = piece >= "A" && piece <= "Z";
  const blackPiece = piece >= "a" && piece <= "z";
  if (session.color === "red") return redPiece;
  if (session.color === "black") return blackPiece;
  return false;
}

function render() {
  ensureBoard();
  document.getElementById("board").classList.toggle("can-move", canMove());
  highlightLayer.replaceChildren();
  pieceLayer.replaceChildren();
  const last = state.lastMove && state.lastMove.length === 4 ? state.lastMove : null;
  if (last) {
    const from = parseSquare(last.slice(0, 2));
    const to = parseSquare(last.slice(2));
    highlightLayer.appendChild(el("circle", { cx: xOf(from.file), cy: yOf(from.rank), r: 23, class: "last-move" }));
    highlightLayer.appendChild(el("circle", { cx: xOf(to.file), cy: yOf(to.rank), r: 23, class: "last-move" }));
  }
  if (selected && canMove()) {
    for (const move of legalMoves) {
      if (move.length !== 4 || move.slice(0, 2) !== selected) continue;
      const to = parseSquare(move.slice(2));
      const occupied = pieceAt(to.file, to.rank) !== "";
      if (occupied) {
        highlightLayer.appendChild(el("circle", {
          cx: xOf(to.file), cy: yOf(to.rank), r: 22, class: "dest-capture"
        }));
      } else {
        highlightLayer.appendChild(el("circle", {
          cx: xOf(to.file), cy: yOf(to.rank), r: 6, class: "dest"
        }));
      }
    }
  }
  let king = null;
  if (state.inCheck) {
    const target = state.sideToMove === "black" ? "k" : "K";
    for (let rank = 0; rank < 10 && !king; rank++) {
      for (let file = 0; file < 9; file++) {
        if (pieceAt(file, rank) === target) {
          king = { file: file, rank: rank };
          break;
        }
      }
    }
  }
  for (let rank = 0; rank < 10; rank++) {
    for (let file = 0; file < 9; file++) {
      const piece = pieceAt(file, rank);
      if (!piece) continue;
      const x = xOf(file);
      const y = yOf(rank);
      pieceLayer.appendChild(el("circle", { cx: x, cy: y + 1.5, r: 20, class: "piece-shadow" }));
      const red = piece >= "A" && piece <= "Z";
      pieceLayer.appendChild(el("circle", { cx: x, cy: y, r: 20, class: red ? "disc red" : "disc black" }));
      pieceLayer.appendChild(textNode(GLYPH[piece] || piece, x, y, red ? "piece-label red" : "piece-label black"));
      if (selected === squareName(file, rank))
        pieceLayer.appendChild(el("circle", { cx: x, cy: y, r: 23, class: "selected-ring" }));
      if (king && king.file === file && king.rank === rank)
        pieceLayer.appendChild(el("circle", { cx: x, cy: y, r: 24, class: "check-ring" }));
    }
  }
  paintStatus();
  paintLobbyActive();
}

function onBoardClick(event) {
  const node = event.target && event.target.closest ? event.target.closest(".hit") : null;
  if (!node) return;
  onSquare(Number(node.dataset.file), Number(node.dataset.rank));
}

function onSquare(file, rank) {
  if (!canMove() || busy) return;
  const name = squareName(file, rank);
  if (selected) {
    const candidate = selected + name;
    if (legalMoves.indexOf(candidate) >= 0) {
      void playMove(candidate);
      return;
    }
  }
  const piece = pieceAt(file, rank);
  const hasMove = legalMoves.some(function (move) { return move.slice(0, 2) === name; });
  if (piece && isOwn(piece) && hasMove) {
    selected = selected === name ? null : name;
    render();
    return;
  }
  if (selected) {
    selected = null;
    render();
  }
}

async function playMove(notation) {
  if (busy || !session.token || !session.roomId) return;
  busy = true;
  selected = null;
  render();
  try {
    const result = await postJson("/api/rooms/" + session.roomId + "/moves", {
      move: notation,
      token: session.token
    });
    if (!result.ok) {
      setNotice((result.data && result.data.error) || "走法被拒绝。 Move rejected.", "error");
      render();
      return;
    }
    await applyState(result.data);
  } finally {
    busy = false;
  }
}

async function applyState(next) {
  if (!next || !Array.isArray(next.board)) return;
  const ply = Array.isArray(next.history) ? next.history.length : 0;
  const sameRoom = !preview && state && next.id && state.id === next.id;
  if (sameRoom && ply < seenPly) return;
  if (sameRoom && ply === seenPly && next.fen === state.fen && next.status === state.status
      && !!next.inCheck === !!state.inCheck
      && JSON.stringify(next.seats) === JSON.stringify(state.seats))
    return;
  if (!sameRoom || ply !== seenPly) selected = null;
  seenPly = ply;
  preview = false;
  state = next;
  await loadLegal();
  render();
}

async function loadLegal() {
  const ticket = ++legalTicket;
  legalMoves = [];
  if (preview || !state.id || state.status !== "ongoing") return;
  const response = await fetch("/api/rooms/" + state.id + "/moves");
  if (ticket !== legalTicket || !response.ok) return;
  const data = await response.json();
  if (ticket !== legalTicket || !Array.isArray(data)) return;
  legalMoves = data;
}

function setNotice(text, kind) {
  notice = { text: text, kind: kind, ply: seenPly };
}

function paintStatus() {
  const bar = document.getElementById("statusBar");
  const message = document.getElementById("message");
  const conn = document.getElementById("conn").textContent;
  if (preview || !state.id) {
    bar.textContent = "象棋擂台 · 选择执子颜色和难度。 Pick a color and difficulty.";
    if (notice) paintNotice(message);
    else {
      message.textContent = "点击「新对局」与内置 AI 对弈，或在右侧观战。 Start a game, or spectate a room.";
      message.className = "message";
    }
    return;
  }
  const red = state.seats && state.seats.red;
  const black = state.seats && state.seats.black;
  bar.textContent = (state.name || "房间") + " · 红 " + kindLabel(red) + " vs 黑 " + kindLabel(black) + " · " + conn;
  if (notice && notice.ply === seenPly) {
    paintNotice(message);
    return;
  }
  notice = null;
  if (state.status === "redWins" || state.status === "blackWins" || state.status === "draw") {
    message.textContent = state.status === "redWins" ? "红方胜 · Red wins"
      : state.status === "blackWins" ? "黑方胜 · Black wins" : "和棋 · Draw";
    message.className = "message over";
    return;
  }
  let text = state.inCheck ? "将军 Check · " : "";
  if (canMove()) text += "轮到你 Your turn";
  else if (isAiTurn()) text += "电脑思考中 AI is thinking";
  else if (!session.token) text += "观战中 Spectating · " + sideText();
  else text += "等待对方 Waiting · " + sideText();
  message.textContent = text;
  message.className = state.inCheck ? "message check" : "message";
}

function paintNotice(message) {
  message.textContent = notice.text;
  message.className = "message " + (notice.kind || "error");
}

function sideText() {
  return state.sideToMove === "black" ? "黑方走 Black to move" : "红方走 Red to move";
}

function isAiTurn() {
  if (!state.seats || state.status !== "ongoing") return false;
  const seat = state.sideToMove === "black" ? state.seats.black : state.seats.red;
  return !!(seat && seat.kind === "ai");
}

function kindLabel(seat) {
  if (!seat || seat.kind === "open" || seat.occupied === false) return "空";
  if (seat.kind === "ai") return "AI·" + diffLabel(seat.difficulty);
  if (seat.kind === "bot") return "Bot";
  return "人";
}

function diffLabel(value) {
  if (value === "easy") return "简单";
  if (value === "hard") return "困难";
  return "中等";
}

function statusLabel(status) {
  if (status === "redWins") return "红胜";
  if (status === "blackWins") return "黑胜";
  if (status === "draw") return "和棋";
  return "进行中";
}

function paintLobbyActive() {
  const items = document.querySelectorAll("#roomList .room");
  items.forEach(function (item) {
    item.classList.toggle("active", item.dataset.id === session.roomId);
  });
}

async function postJson(url, body) {
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
  return { ok: response.ok, status: response.status, data: data };
}

function saveSession() {
  if (!session.roomId) {
    sessionStorage.removeItem(SESSION_KEY);
    return;
  }
  sessionStorage.setItem(SESSION_KEY, JSON.stringify(session));
}

async function newGame(event) {
  event.preventDefault();
  const button = document.getElementById("newGame");
  button.disabled = true;
  try {
    const humanColor = document.getElementById("color").value;
    const difficulty = document.getElementById("difficulty").value;
    const result = await postJson("/api/games/vs-ai", { humanColor: humanColor, difficulty: difficulty });
    if (!result.ok) {
      setNotice((result.data && result.data.error) || "无法开始对局。 Could not start a game.", "error");
      render();
      return;
    }
    session = {
      roomId: result.data.roomId,
      token: result.data.token,
      color: humanColor,
      spectating: false
    };
    saveSession();
    selected = null;
    seenPly = -1;
    notice = null;
    await applyState(result.data.state);
    await watchRoom(result.data.roomId);
    await syncRoom(result.data.roomId);
    await loadLobby();
  } finally {
    button.disabled = false;
  }
}

async function spectate(roomId) {
  session = { roomId: roomId, token: null, color: null, spectating: true };
  saveSession();
  selected = null;
  seenPly = -1;
  notice = null;
  const response = await fetch("/api/rooms/" + roomId);
  if (!response.ok) {
    setNotice("房间不存在。 Room not found.", "error");
    render();
    return;
  }
  preview = false;
  await applyState(await response.json());
  await watchRoom(roomId);
  await syncRoom(roomId);
  paintLobbyActive();
}

async function syncRoom(roomId) {
  if (session.roomId !== roomId) return;
  const response = await fetch("/api/rooms/" + roomId);
  if (response.ok) await applyState(await response.json());
}

function setConn(mode) {
  const el = document.getElementById("conn");
  if (mode === "live") el.textContent = "实时 Live";
  else if (mode === "poll") el.textContent = "轮询 Polling";
  else el.textContent = "未连接 Offline";
}

function stopPoll() {
  if (pollTimer) clearInterval(pollTimer);
  pollTimer = null;
}

function startPoll(roomId) {
  stopPoll();
  setConn("poll");
  pollTimer = setInterval(function () { void syncRoom(roomId); }, 1000);
}

function ensureConnection() {
  if (typeof signalR === "undefined") return null;
  if (!connection) {
    connection = new signalR.HubConnectionBuilder()
      .withUrl("/hub/game")
      .withAutomaticReconnect()
      .build();
    connection.on("state", function (next) { void applyState(next); });
    connection.onreconnected(function () {
      if (!session.roomId) return;
      connection.invoke("JoinRoom", session.roomId).then(function () {
        joinedRoom = session.roomId;
        stopPoll();
        setConn("live");
        return syncRoom(session.roomId);
      }).catch(function () {
        startPoll(session.roomId);
      });
    });
    connection.onclose(function () {
      if (session.roomId) startPoll(session.roomId);
      else setConn("offline");
    });
  }
  return connection;
}

async function watchRoom(roomId) {
  stopPoll();
  const conn = ensureConnection();
  if (!conn) {
    startPoll(roomId);
    render();
    return;
  }
  try {
    if (conn.state !== signalR.HubConnectionState.Connected)
      await conn.start();
    if (joinedRoom && joinedRoom !== roomId)
      await conn.invoke("LeaveRoom", joinedRoom);
    await conn.invoke("JoinRoom", roomId);
    joinedRoom = roomId;
    setConn("live");
  } catch (err) {
    console.warn("SignalR unavailable, polling.", err);
    startPoll(roomId);
  }
  render();
}

async function loadLobby() {
  const response = await fetch("/api/rooms");
  if (!response.ok) return;
  const rooms = await response.json();
  const list = document.getElementById("roomList");
  list.replaceChildren();
  if (!Array.isArray(rooms) || rooms.length === 0) {
    const empty = document.createElement("li");
    empty.className = "empty";
    empty.textContent = "暂无房间。 No rooms yet.";
    list.appendChild(empty);
    return;
  }
  for (const room of rooms) {
    const item = document.createElement("li");
    item.className = session.roomId === room.id ? "room active" : "room";
    item.dataset.id = room.id;
    const title = document.createElement("div");
    title.className = "room-name";
    title.textContent = room.name || room.id;
    const meta = document.createElement("div");
    meta.className = "room-meta";
    const red = room.seats && room.seats.red;
    const black = room.seats && room.seats.black;
    meta.textContent = "红 " + kindLabel(red) + " · 黑 " + kindLabel(black)
      + " · " + statusLabel(room.status)
      + " · " + (room.sideToMove === "black" ? "黑走" : "红走")
      + " · " + (room.moveCount || 0) + "着";
    const button = document.createElement("button");
    button.type = "button";
    button.className = "secondary";
    button.textContent = "观战 Spectate";
    button.addEventListener("click", function () { void spectate(room.id); });
    item.append(title, meta, button);
    list.appendChild(item);
  }
}

async function restore() {
  let saved = null;
  try { saved = JSON.parse(sessionStorage.getItem(SESSION_KEY) || "null"); }
  catch { saved = null; }
  if (!saved || !saved.roomId) return;
  const response = await fetch("/api/rooms/" + saved.roomId);
  if (!response.ok) {
    sessionStorage.removeItem(SESSION_KEY);
    return;
  }
  session = saved;
  preview = false;
  await applyState(await response.json());
  await watchRoom(saved.roomId);
}

async function init() {
  ensureBoard();
  render();
  document.getElementById("newGameForm").addEventListener("submit", function (event) { void newGame(event); });
  document.getElementById("refresh").addEventListener("click", function () { void loadLobby(); });
  try { await restore(); }
  catch (err) { console.warn(err); }
  await loadLobby();
  setInterval(function () { void loadLobby(); }, 8000);
}

void init();
