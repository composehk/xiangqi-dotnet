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
    private readonly ContinueStore _store;
    private readonly IPaymentVerifier _verifier;
    private readonly PaymentOptions _options;
    private readonly IEngine _engine = new AlphaBetaEngine();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ILogger<ContinueService> _logger;

    public ContinueService(ContinueStore store, IPaymentVerifier verifier, PaymentOptions options, ILogger<ContinueService> logger)
    {
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
            throw new ApiException(400, ex.Message);
        }
    }

    public ContinueSessionDto Create(CreateContinueRequest? request)
    {
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
            throw new ApiException(400, "Difficulty must be easy, medium, or hard.");
        string color = string.IsNullOrWhiteSpace(request?.HumanColor)
            ? (probe.SideToMove == Color.Red ? "red" : "black")
            : request!.HumanColor!.Trim().ToLowerInvariant();
        if (color is not ("red" or "black"))
            throw new ApiException(400, "humanColor must be red or black.");

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
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
        return Project(s, includeToken: true);
    }

    public ContinueSessionDto Get(string id) => Project(Load(id));

    public ContinueSessionDto Authorize(string id, ContinueAuthRequest? request)
    {
        RequireConfigured();
        var s = Load(id);
        RequireToken(s, request?.Token);
        if (s.Status != "pending_auth" && s.Status != "pending_payment")
            throw new ApiException(409, "会话已激活或已结束 / session already activated");
        long ts = request?.Timestamp ?? 0;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (ts <= 0 || Math.Abs(now - ts) > _options.SignatureMaxAgeSeconds || ts < s.CreatedAt - 60)
            throw new ApiException(400, "签名时间戳已过期，请重新签名 / timestamp outside freshness window");
        string message = SignMessage(s.Id, ts);
        var check = KaspaMessageSigner.Verify(request?.Address, message, request?.Signature, _options.Network);
        if (!check.Valid)
            throw new ApiException(400, check.Error ?? "签名无效");
        s.Address = request!.Address!.Trim().ToLowerInvariant();
        s.Signature = request.Signature!.Trim();
        s.Timestamp = ts;
        s.Status = "pending_payment";
        s.UpdatedAt = now;
        _store.Update(s);
        return Project(s);
    }

    public async Task<(int Status, ContinueSessionDto Session)> SubmitPaymentAsync(string id, ContinuePaymentRequest? request, CancellationToken ct)
    {
        RequireConfigured();
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var s = Load(id);
            RequireToken(s, request?.Token);
            if (s.Status == "active" || s.Status == "finished")
                return (200, Project(s));
            if (s.Status != "pending_payment" || string.IsNullOrEmpty(s.Address))
                throw new ApiException(409, "请先完成钱包签名 / sign with your wallet first");
            string txid = (request?.Txid ?? "").Trim().ToLowerInvariant();
            if (!KaspaPaymentVerifier.IsTxid(txid))
                throw new ApiException(400, "txid 格式无效（64 位十六进制） / invalid txid");
            if (_store.IsTxidUsed(txid))
                throw new ApiException(409, "该交易已被使用 / txid already used");

            var result = await _verifier.VerifyAsync(txid, s.Address, ct);
            if (result.Pending)
                return (202, Project(s, info: result.Error));
            if (!result.Ok)
                throw new ApiException(400, result.Error ?? "支付验证失败");
            if (!_store.TryConsumeTxid(s, txid, result.AmountSompi, result.Confirmations))
                throw new ApiException(409, "该交易已被使用 / txid already used");
            _logger.LogInformation("Continue session {Id} activated by tx {Txid}", s.Id, txid);
            s = Load(id);
            await AiReplyAsync(s, ct);
            return (200, Project(s));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ContinueSessionDto> PlayAsync(string id, ContinueMoveRequest? request, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
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
            Move move;
            try
            {
                move = Move.Parse((request?.Move ?? "").Trim().ToLowerInvariant());
            }
            catch (FormatException)
            {
                try { move = ChineseNotation.Parse(game.Board, request?.Move ?? ""); }
                catch (FormatException ex) { throw new ApiException(400, ex.Message); }
            }
            if (!game.Board.IsLegal(move))
                throw new ApiException(400, $"Illegal move {move}.");
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
        s.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
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
        _store.Get(id) ?? throw new ApiException(404, "Session not found.");

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
            s.Id, s.Status, SignMessage(s.Id, s.Timestamp), s.Timestamp, s.Nonce,
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
            : PaymentCheck.Fail("invalid txid"));
}
