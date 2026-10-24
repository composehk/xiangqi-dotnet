using System.Collections.Concurrent;
using System.Security.Cryptography;
using Xiangqi.Core;
using Xiangqi.Engine;
using Xiangqi.Server.Kaspa;

namespace Xiangqi.Server;

public sealed record ParseRecordRequest(string? Fen, string? Moves);

public sealed record ParseRecordResponse(string StartFen, string[] Iccs, string[] Chinese, string[] Fens);

public sealed record CreateContinueRequest(string? Fen, string? Moves, int? Ply, string? Difficulty, string? HumanColor);

public sealed record ContinueAuthRequest(string? Token, string? Address, string? Signature, long? Timestamp);

public sealed record ContinuePaymentRequest(string? Token, string? Txid);

public sealed record ContinueMoveRequest(string? Token, string? Move);

public sealed record PaymentConfigDto(bool Enabled, string? PayToAddress, long AmountSompi, decimal AmountKas, string Network,
    int MinConfirmations, int SignatureMaxAgeSeconds, string FeeLabel);

public sealed record ContinueStateDto(string Fen, string[][] Board, string SideToMove, string Status, bool InCheck,
    string? LastMove, string[] History, string[] HistoryChinese, string[] LegalMoves);

public sealed record ContinueSessionDto(
    string Id, string Status, string Message, long Timestamp, string Nonce, string? PayToAddress, long AmountSompi,
    string Network, int MinConfirmations, string? Address, string? Txid, int Ply, string StartFen, string HumanColor,
    string Difficulty, string? Token, string? Info, ContinueStateDto State);

/// <summary>断点续弈: pay-per-session continuation of any position against the built-in AI.</summary>
public sealed class ContinueService
{
    public const string FeeLabel = "续弈服务费";

    // Input size limits (all checked before any parsing / DB / network work).
    public const int MaxFenLength = 120;
    public const int MaxMovesLength = 20_000;
    public const int MaxAddressLength = 100;
    public const int MaxSignatureLength = 256;
    public const int MaxTokenLength = 64;
    public const int MaxTxidLength = 80;
    public const int MaxMoveLength = 64;
    public const int MaxOptionLength = 16;

    private readonly ContinueStore _store;
    private readonly IPaymentVerifier _verifier;
    private readonly PaymentOptions _options;
    private readonly IEngine _engine = new AlphaBetaEngine();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, long> _lastLookup = new();
    private readonly ILogger<ContinueService> _logger;
    private readonly TimeProvider _time;
    /// <summary>UTC ticks of the last opportunistic cache sweep. 0 means one has not run yet.</summary>
    private long _lastCleanupTicks;

    public ContinueService(ContinueStore store, IPaymentVerifier verifier, PaymentOptions options, ILogger<ContinueService> logger,
        TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _store = store;
        _verifier = verifier;
        _options = options;
        _logger = logger;
    }

    public static string SignMessage(string sessionId, long timestamp) => $"XiangqiArena:Continue:{sessionId}:{timestamp}";

    public PaymentConfigDto Config() => new(
        _options.IsConfigured, _options.IsConfigured ? _options.PayToAddress : null, _options.PriceSompi,
        _options.PriceSompi / 100_000_000m, _options.Network, _options.MinConfirmations, _options.SignatureMaxAgeSeconds, FeeLabel);

    public static ParseRecordResponse ParseRecord(ParseRecordRequest? request)
    {
        Limit(request?.Fen, MaxFenLength, "fen");
        Limit(request?.Moves, MaxMovesLength, "moves");
        string fen = string.IsNullOrWhiteSpace(request?.Fen) ? Board.StartFen : request!.Fen!.Trim();
        try
        {
            fen = Board.FromFen(fen).ToFen();
            var moves = GameRecord.ParseMoves(fen, request?.Moves ?? "");
            var (_, fens) = GameRecord.Replay(fen, moves);
            return new ParseRecordResponse(fen, moves.Select(m => m.ToString()).ToArray(),
                GameRecord.ToChinese(fen, moves).ToArray(), fens.ToArray());
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            throw new ApiException(400, $"局面或着法无效 / Invalid position or moves: {ex.Message}");
        }
    }

    public ContinueSessionDto Create(CreateContinueRequest? request)
    {
        RequireConfigured(); // no pay-to address (or Payment disabled) => the paid entry is closed: 503, nothing is stored
        Limit(request?.Difficulty, MaxOptionLength, "difficulty");
        Limit(request?.HumanColor, MaxOptionLength, "humanColor");
        var parsed = ParseRecord(new ParseRecordRequest(request?.Fen, request?.Moves));
        int ply = request?.Ply ?? parsed.Iccs.Length;
        if (ply < 0 || ply > parsed.Iccs.Length)
            throw new ApiException(400, $"ply 必须在 0..{parsed.Iccs.Length} 之间 / ply out of range");
        string startFen = parsed.Fens[ply];
        var probe = new Game(startFen);
        if (probe.Status != GameStatus.Ongoing)
            throw new ApiException(400, "该断点局面已终局，无法续弈 / position is already decided");

        string difficulty = (request?.Difficulty ?? "medium").Trim().ToLowerInvariant();
        if (difficulty is not ("easy" or "medium" or "hard"))
            throw new ApiException(400, "难度必须是 easy、medium 或 hard / Difficulty must be easy, medium, or hard.");
        string color = string.IsNullOrWhiteSpace(request?.HumanColor)
            ? (probe.SideToMove == Color.Red ? "red" : "black")
            : request!.HumanColor!.Trim().ToLowerInvariant();
        if (color is not ("red" or "black"))
            throw new ApiException(400, "humanColor 必须是 red 或 black / humanColor must be red or black.");

        long now = _time.GetUtcNow().ToUnixTimeSeconds();
        var s = new ContinueSession
        {
            Id = Guid.NewGuid().ToString("N"),
            Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(),
            Timestamp = now,
            CreatedAt = now,
            SourceFen = parsed.StartFen,
            SourceMoves = string.Join(' ', parsed.Iccs),
            Ply = ply,
            StartFen = startFen,
            Difficulty = difficulty,
            HumanColor = color,
            Status = "pending_auth",
            UpdatedAt = now,
        };
        _store.Insert(s);
        var created = Project(s, includeToken: true);
        CleanupIfDue();
        return created;
    }

    public ContinueSessionDto Get(string id) => Project(Load(id));

    public ContinueSessionDto Authorize(string id, ContinueAuthRequest? request)
    {
        RequireConfigured();
        Limit(request?.Token, MaxTokenLength, "token");
        Limit(request?.Address, MaxAddressLength, "address");
        Limit(request?.Signature, MaxSignatureLength, "signature");
        var s0 = Load(id);
        var gate = _locks.GetOrAdd(s0.Id, _ => new SemaphoreSlim(1, 1));
        // Same per-session lock as payment/moves: a late re-sign must not overwrite a just-activated session.
        gate.Wait();
        try
        {
            return AuthorizeLocked(id, request);
        }
        finally
        {
            gate.Release();
        }
    }

    private ContinueSessionDto AuthorizeLocked(string id, ContinueAuthRequest? request)
    {
        var s = Load(id);
        RequireToken(s, request?.Token);
        if (s.Status != "pending_auth" && s.Status != "pending_payment")
            throw new ApiException(409, "会话已激活或已结束 / session already activated");
        RequireNotExpired(s);
        long ts = request?.Timestamp ?? 0;
        long now = _time.GetUtcNow().ToUnixTimeSeconds();
        if (ts <= 0 || Math.Abs(now - ts) > _options.SignatureMaxAgeSeconds || ts < s.CreatedAt - 60)
            throw new ApiException(400, "签名时间戳已过期，请重新签名 / timestamp outside freshness window");
        string message = SignMessage(s.Id, ts);
        var check = KaspaMessageSigner.Verify(request?.Address, message, request?.Signature, _options.Network);
        if (!check.Valid)
            throw new ApiException(400, check.Error ?? "签名无效 / invalid signature");
        s.Address = request!.Address!.Trim().ToLowerInvariant();
        s.Signature = request.Signature!.Trim();
        s.Timestamp = ts;
        s.Status = "pending_payment";
        s.UpdatedAt = now;
        if (!_store.TryAuthorize(s))
            throw new ApiException(409, "会话已激活或已结束 / session already activated");
        return Project(s);
    }

    public async Task<(int Status, ContinueSessionDto Session)> SubmitPaymentAsync(string id, ContinuePaymentRequest? request, CancellationToken ct)
    {
        RequireConfigured();
        Limit(request?.Token, MaxTokenLength, "token");
        Limit(request?.Txid, MaxTxidLength, "txid");
        var known = Load(id); // 404 for unknown ids BEFORE a lock is allocated (bounded lock table)
        var gate = _locks.GetOrAdd(known.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var s = Load(id);
            RequireToken(s, request?.Token);
            if (s.Status == "active" || s.Status == "finished")
                return (200, Project(s));
            if (s.Status != "pending_payment" || string.IsNullOrEmpty(s.Address))
                throw new ApiException(409, "请先完成钱包签名 / sign with your wallet first");
            RequirePaymentWindow(s);
            string txid = (request?.Txid ?? "").Trim().ToLowerInvariant();
            if (!KaspaPaymentVerifier.IsTxid(txid))
                throw new ApiException(400, "txid 格式无效（64 位十六进制） / invalid txid");
            // Same txid + this session is a lost-response retry, not a conflict.
            string? bound = _store.GetTxidSession(txid);
            if (bound == s.Id)
                return (200, Project(Load(id)));
            if (bound != null)
                throw new ApiException(409, "该交易已被使用 / txid already used");

            // Throttle upstream lookups per session (the UI polls every 5 s).
            long nowTicks = Environment.TickCount64;
            if (_lastLookup.TryGetValue(s.Id, out long last) && nowTicks - last < _options.PaymentMinIntervalSeconds * 1000L)
                throw new ApiException(429, "请求过于频繁，请稍后再试 / too many payment checks, slow down");
            _lastLookup[s.Id] = nowTicks;

            var result = await _verifier.VerifyAsync(txid, s.Address, ct);
            if (result.Pending)
                return (202, Project(s, info: result.Error ?? "等待确认 / waiting for confirmation"));
            if (!result.Ok)
                throw new ApiException(400, result.Error ?? "支付验证失败 / payment verification failed");
            var consumed = _store.TryConsumeTxid(s, txid, result.AmountSompi, result.Confirmations, requirePendingPayment: true);
            if (consumed == ConsumeResult.SessionNotPayable)
                throw new ApiException(409, "会话不可支付 / session is not payable");
            if (consumed == ConsumeResult.TxidUsed)
            {
                if (_store.GetTxidSession(txid) == s.Id)
                    return (200, Project(Load(id)));
                throw new ApiException(409, "该交易已被使用 / txid already used");
            }
            _logger.LogInformation("Continue session {Id} activated by tx {Txid}", s.Id, txid);
            s = Load(id);
            await AiReplyAsync(s, ct);
            return (200, Project(s));
        }
        finally
        {
            gate.Release();
            CleanupIfDue();
        }
    }

    public async Task<ContinueSessionDto> PlayAsync(string id, ContinueMoveRequest? request, CancellationToken ct)
    {
        CleanupIfDue();
        Limit(request?.Token, MaxTokenLength, "token");
        Limit(request?.Move, MaxMoveLength, "move");
        var known = Load(id);
        var gate = _locks.GetOrAdd(known.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var s = Load(id);
            RequireToken(s, request?.Token);
            if (s.Status is "pending_auth" or "pending_payment")
                throw new ApiException(402, $"请先支付{FeeLabel}（1 KAS）/ payment required");
            var game = Rebuild(s);
            if (game.Status != GameStatus.Ongoing)
                throw new ApiException(409, "对局已结束 / game over");
            if (ColorName(game.SideToMove) != s.HumanColor)
                throw new ApiException(409, "现在不是你的回合 / not your turn");
            string moveText = (request?.Move ?? "").Trim();
            Move move;
            try
            {
                move = Move.Parse(moveText.ToLowerInvariant());
            }
            catch (FormatException)
            {
                try { move = ChineseNotation.Parse(game.Board, moveText); }
                catch (FormatException)
                {
                    throw new ApiException(400, $"无法识别或非法的着法「{moveText}」 / Illegal or unrecognised move '{moveText}'.");
                }
            }
            if (!game.Board.IsLegal(move))
                throw new ApiException(400, $"非法着法 {move} / Illegal move {move}.");
            game.MakeMove(move);
            Save(s, game);
            await AiReplyAsync(s, ct);
            return Project(s);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task AiReplyAsync(ContinueSession s, CancellationToken ct)
    {
        var game = Rebuild(s);
        if (game.Status != GameStatus.Ongoing || ColorName(game.SideToMove) == s.HumanColor)
            return;
        var difficulty = s.Difficulty switch { "easy" => Difficulty.Easy, "hard" => Difficulty.Hard, _ => Difficulty.Medium };
        var result = await _engine.SearchAsync(game.Board.Clone(), SearchLimits.For(difficulty), ct);
        if (!result.BestMove.IsNone && game.Board.IsLegal(result.BestMove))
            game.MakeMove(result.BestMove);
        Save(s, game);
    }

    private void Save(ContinueSession s, Game game)
    {
        s.Moves = string.Join(' ', game.History.Select(m => m.ToString()));
        s.Result = StatusName(game.Status);
        if (game.Status != GameStatus.Ongoing)
            s.Status = "finished";
        s.UpdatedAt = _time.GetUtcNow().ToUnixTimeSeconds();
        _store.Update(s);
    }

    private static Game Rebuild(ContinueSession s)
    {
        var game = new Game(s.StartFen);
        foreach (var token in s.Moves.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            game.MakeMove(token);
        return game;
    }

    private ContinueSession Load(string id) =>
        (IsSessionId(id) ? _store.Get(id) : null) ?? throw new ApiException(404, "找不到续弈会话 / Continue session not found.");

    /// <summary>Session ids are Guid "N" strings: exactly 32 lowercase hex characters.</summary>
    public static bool IsSessionId(string? id) =>
        id is { Length: 32 } && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static void Limit(string? value, int max, string name)
    {
        if (value is not null && value.Length > max)
            throw new ApiException(413, $"{name} 过长（最多 {max} 字符） / {name} too long (max {max})");
    }

    private bool IsExpired(ContinueSession s) =>
        s.Status is "pending_auth" or "pending_payment"
        && _time.GetUtcNow().ToUnixTimeSeconds() - s.CreatedAt > _options.SessionTtlMinutes * 60L;

    /// <summary>
    /// True when an unpaid session is past both the TTL and the payment grace window.
    /// Inside the grace window a signed pending_payment session may still submit a txid.
    /// </summary>
    private bool IsBeyondPaymentGrace(ContinueSession s)
    {
        if (!IsExpired(s))
            return false;
        long age = _time.GetUtcNow().ToUnixTimeSeconds() - s.CreatedAt;
        long limit = ((long)_options.SessionTtlMinutes + _options.ExpiredPaymentGraceMinutes) * 60L;
        return age > limit;
    }

    /// <summary>At most one sweep per 60 seconds, driven by <see cref="TimeProvider"/>.</summary>
    private void CleanupIfDue()
    {
        long now = _time.GetUtcNow().UtcTicks;
        long prev = Interlocked.Read(ref _lastCleanupTicks);
        if (prev != 0 && now - prev < TimeSpan.FromSeconds(60).Ticks)
            return;
        if (Interlocked.CompareExchange(ref _lastCleanupTicks, now, prev) != prev)
            return;
        CleanupCaches();
    }

    /// <summary>
    /// Drops cache entries that can no longer be used. A lock is removed only when it is free
    /// (CurrentCount == 1) and the session is gone, finished, or past the payment grace window.
    /// Lookup timestamps older than 10× the per-session payment interval are removed regardless of status.
    /// Returns how many dictionary entries were removed. A lock held by the caller (for example while
    /// status becomes finished) is left for a later sweep.
    /// </summary>
    public int CleanupCaches()
    {
        int removed = 0;
        foreach (var kv in _locks)
        {
            SemaphoreSlim gate = kv.Value;
            // Held locks stay. Checking before TryRemove accepts a tiny race; do not put an entry back.
            if (gate.CurrentCount != 1)
                continue;
            ContinueSession? session = IsSessionId(kv.Key) ? _store.Get(kv.Key) : null;
            if (session is not null && session.Status != "finished" && !IsBeyondPaymentGrace(session))
                continue;
            if (gate.CurrentCount != 1)
                continue;
            if (_locks.TryRemove(new KeyValuePair<string, SemaphoreSlim>(kv.Key, gate)))
            {
                removed++;
                if (gate.CurrentCount == 1)
                    gate.Dispose();
            }
        }

        long nowTicks = Environment.TickCount64;
        long maxAgeMs = 10L * _options.PaymentMinIntervalSeconds * 1000L;
        foreach (var kv in _lastLookup)
        {
            if (nowTicks - kv.Value > maxAgeMs && _lastLookup.TryRemove(kv.Key, out _))
                removed++;
        }
        return removed;
    }

    private void RequireNotExpired(ContinueSession s)
    {
        if (IsExpired(s))
            throw new ApiException(410, "会话已过期，请重新创建 / session expired, create a new one");
    }

    /// <summary>
    /// Payment-only window. A session still inside SessionTtl is unchanged. After that, a signed
    /// pending_payment session may submit until CreatedAt + (SessionTtl + grace). Grace 0 keeps the old 410.
    /// </summary>
    private void RequirePaymentWindow(ContinueSession s)
    {
        if (!IsExpired(s))
            return;
        long age = _time.GetUtcNow().ToUnixTimeSeconds() - s.CreatedAt;
        long limit = ((long)_options.SessionTtlMinutes + _options.ExpiredPaymentGraceMinutes) * 60L;
        if (s.Status == "pending_payment" && !string.IsNullOrEmpty(s.Address) && age <= limit)
            return;
        throw new ApiException(410, "会话已过期，且已超过恢复期。如果您已转账，请保留交易 ID（txid）并联系客服 / session expired beyond the recovery window; if you already paid, keep your txid and contact support");
    }

    private void RequireConfigured()
    {
        if (!_options.IsConfigured)
            throw new ApiException(503, "未开放：未配置收款地址 / payment not configured");
    }

    private static void RequireToken(ContinueSession s, string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(token.Trim()), System.Text.Encoding.ASCII.GetBytes(s.Token)))
            throw new ApiException(403, "会话令牌无效 / invalid session token");
    }

    private ContinueSessionDto Project(ContinueSession s, bool includeToken = false, string? info = null)
    {
        var game = Rebuild(s);
        var history = game.History.ToList();
        bool humanTurn = s.Status == "active" && game.Status == GameStatus.Ongoing && ColorName(game.SideToMove) == s.HumanColor;
        var state = new ContinueStateDto(
            game.Fen, BoardRows(game.Board), ColorName(game.SideToMove), StatusName(game.Status), game.Board.IsInCheck(),
            history.Count > 0 ? history[^1].ToString() : null,
            history.Select(m => m.ToString()).ToArray(),
            GameRecord.ToChinese(s.StartFen, history).ToArray(),
            humanTurn ? game.Board.GenerateLegalMoves().Select(m => m.ToString()).ToArray() : Array.Empty<string>());
        return new ContinueSessionDto(
            s.Id, IsExpired(s) ? "expired" : s.Status, SignMessage(s.Id, s.Timestamp), s.Timestamp, s.Nonce,
            _options.IsConfigured ? _options.PayToAddress : null, _options.PriceSompi, _options.Network, _options.MinConfirmations,
            s.Address, s.Txid, s.Ply, s.StartFen, s.HumanColor, s.Difficulty, includeToken ? s.Token : null, info, state);
    }

    private static string ColorName(Color c) => c == Color.Red ? "red" : "black";

    private static string StatusName(GameStatus status) => status switch
    {
        GameStatus.RedWins => "redWins",
        GameStatus.BlackWins => "blackWins",
        GameStatus.Draw => "draw",
        _ => "ongoing",
    };

    private static string[][] BoardRows(Board board)
    {
        var rows = new string[Board.RankCount][];
        for (int rank = Board.RankCount - 1; rank >= 0; rank--)
        {
            var row = new string[Board.FileCount];
            for (int file = 0; file < Board.FileCount; file++)
            {
                Piece piece = board.GetPiece(file, rank);
                row[file] = piece == Piece.None ? "" : piece.ToFenChar().ToString();
            }
            rows[Board.RankCount - 1 - rank] = row;
        }
        return rows;
    }
}

/// <summary>Development-only verifier (Payment:TestMode). Accepts any well-formed txid. Never used in Production.</summary>
public sealed class TestModePaymentVerifier : IPaymentVerifier
{
    private readonly PaymentOptions _options;
    public TestModePaymentVerifier(PaymentOptions options) => _options = options;
    public Task<PaymentCheck> VerifyAsync(string txid, string payerAddress, CancellationToken ct = default) =>
        Task.FromResult(KaspaPaymentVerifier.IsTxid(txid)
            ? new PaymentCheck(true, false, null, _options.PriceSompi, _options.MinConfirmations, new[] { payerAddress })
            : PaymentCheck.Fail("txid 格式无效 / invalid txid"));
}
