using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xiangqi.Core;
using Xiangqi.Engine;

namespace Xiangqi.Server;

internal enum SeatKind
{
    Open,
    Human,
    BuiltinAi,
    ExternalBot
}

internal sealed class Seat
{
    public Seat(SeatKind kind, Difficulty difficulty, string? token)
    {
        Kind = kind;
        Difficulty = difficulty;
        Token = token;
    }

    public SeatKind Kind { get; set; }
    public Difficulty Difficulty { get; set; }
    public string? Token { get; set; }
}

internal sealed class Room
{
    public Room(Guid id, string name, Game game, Seat red, Seat black, bool isPublic)
    {
        Id = id;
        Name = name;
        CreatedAt = DateTimeOffset.UtcNow;
        LastActivity = CreatedAt;
        Game = game;
        Red = red;
        Black = black;
        IsPublic = isPublic;
    }

    public Guid Id { get; }
    public string Name { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset LastActivity { get; set; }
    public Game Game { get; }
    public Seat Red { get; }
    public Seat Black { get; }
    public bool IsPublic { get; }
    public object Gate { get; } = new();
    public SemaphoreSlim PublishLock { get; } = new(1, 1);
    public bool AiRunning { get; set; }
    public long Version { get; set; }
}

/// <summary>Limits for the anonymous public arena. Bound from configuration section "Arena".</summary>
public sealed class ArenaOptions
{
    public int MaxRooms { get; set; } = 300;
    public bool AllowAiVsAi { get; set; } = false;
    public int RoomIdleMinutes { get; set; } = 60;
    public int FinishedRoomMinutes { get; set; } = 10;
    public int SweepIntervalSeconds { get; set; } = 60;
    /// <summary>Per client IP, POST /api/rooms and POST /api/games/vs-ai. 0 or less disables.</summary>
    public int CreatePerMinute { get; set; } = 20;
    /// <summary>Per client IP, POST /api/rooms/{id}/moves and /join. 0 or less disables.</summary>
    public int ActionPerMinute { get; set; } = 240;
}

/// <summary>
/// In-memory arena. Rooms live in a <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// and every mutation of a room takes that room's lock.
/// </summary>
public sealed class ArenaService : IHostedService
{
    private readonly ConcurrentDictionary<Guid, Room> _rooms = new();
    private readonly AlphaBetaEngine _engine = new();
    private readonly IHubContext<GameHub> _hub;
    private readonly ILogger<ArenaService> _logger;
    private readonly ArenaOptions _options;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _sweepLoop;

    public ArenaService(IHubContext<GameHub> hub, ILogger<ArenaService> logger, IOptions<ArenaOptions> options)
    {
        _hub = hub;
        _logger = logger;
        _options = options.Value;
    }

    public static string GroupName(Guid id) => "room:" + id.ToString("D");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.SweepIntervalSeconds > 0)
            _sweepLoop = Task.Run(() => SweepLoopAsync());
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdown.Cancel();
        if (_sweepLoop is not null)
            await _sweepLoop.ConfigureAwait(false);
    }

    private async Task SweepLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.SweepIntervalSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    SweepExpired(DateTimeOffset.UtcNow);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Arena room sweep failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host is stopping.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Arena room sweep loop failed.");
        }
    }

    public bool Exists(Guid id) => _rooms.ContainsKey(id);

    public CreateRoomResponseDto CreateRoom(CreateRoomRequest? request)
    {
        if (request is null)
            throw new ApiException(400, "Request body is required.");

        SeatKind redKind = ParseKind(request.RedKind, allowOpen: true);
        SeatKind blackKind = ParseKind(request.BlackKind, allowOpen: true);
        if (redKind == SeatKind.BuiltinAi && blackKind == SeatKind.BuiltinAi && !_options.AllowAiVsAi)
            throw new ApiException(403, "不允许创建 AI 对 AI 房间 / AI-vs-AI rooms are disabled");

        Difficulty difficulty = ParseDifficulty(request.Difficulty);
        return AddRoom(request.Name, redKind, blackKind, difficulty, isPublic: true);
    }

    public VsAiResponseDto CreateVsAi(VsAiRequest? request)
    {
        if (request is null)
            throw new ApiException(400, "Request body is required.");

        bool humanIsRed = ParseHumanSide(request.HumanColor);
        Difficulty difficulty = ParseDifficulty(request.Difficulty);
        string name = "人机 · 执" + (humanIsRed ? "红" : "黑") + " · " + DifficultyLabel(difficulty);
        SeatKind red = humanIsRed ? SeatKind.Human : SeatKind.BuiltinAi;
        SeatKind black = humanIsRed ? SeatKind.BuiltinAi : SeatKind.Human;
        CreateRoomResponseDto created = AddRoom(name, red, black, difficulty, isPublic: false);
        string? token = humanIsRed ? created.Tokens.Red : created.Tokens.Black;
        if (string.IsNullOrEmpty(token))
            throw new ApiException(500, "Human seat has no token.");

        return new VsAiResponseDto(created.Id, token, created.State);
    }

    public IReadOnlyList<RoomSummaryDto> List()
    {
        Room[] rooms = _rooms.Values.ToArray();
        Array.Sort(rooms, (a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
        var list = new List<RoomSummaryDto>(rooms.Length);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (Room room in rooms)
        {
            lock (room.Gate)
            {
                if (!room.IsPublic || IsExpiredLocked(room, now))
                    continue;

                list.Add(new RoomSummaryDto(
                    room.Id,
                    room.Name,
                    StatusName(room.Game.Status),
                    ColorName(room.Game.SideToMove),
                    room.Game.History.Count,
                    ProjectSeats(room)));
            }
        }

        return list;
    }

    public GameStateDto Get(Guid id) => Project(Require(id));

    public string[] LegalMoves(Guid id)
    {
        Room room = Require(id);
        lock (room.Gate)
        {
            if (room.Game.Status != GameStatus.Ongoing)
                return Array.Empty<string>();

            List<Move> moves = room.Game.Board.GenerateLegalMoves();
            var text = new string[moves.Count];
            for (int i = 0; i < moves.Count; i++)
                text[i] = moves[i].ToString();
            return text;
        }
    }

    public async Task<GameStateDto> PlayAsync(Guid id, MoveRequest? request)
    {
        if (request is null)
            throw new ApiException(400, "Request body is required.");

        Move move;
        try
        {
            move = Move.Parse((request.Move ?? string.Empty).Trim());
        }
        catch (FormatException ex)
        {
            throw new ApiException(400, ex.Message);
        }

        Room room = Require(id);
        GameStateDto state;
        long version;
        lock (room.Gate)
        {
            Color side = room.Game.SideToMove;
            Seat seat = side == Color.Red ? room.Red : room.Black;
            if (!TokenOwns(seat, request.Token))
                throw new ApiException(403, "Token does not own the side to move.");

            if (room.Game.Status != GameStatus.Ongoing)
                throw new ApiException(400, "The game is already over.");

            if (!room.Game.Board.IsLegal(move))
                throw new ApiException(400, $"Illegal move {move}.");

            room.Game.MakeMove(move);
            room.LastActivity = DateTimeOffset.UtcNow;
            version = ++room.Version;
            state = ProjectLocked(room);
        }

        ScheduleAi(room);
        await PublishAsync(room, version);
        return state;
    }

    public async Task<JoinResponseDto> JoinAsync(Guid id, JoinRequest? request)
    {
        if (request is null)
            throw new ApiException(400, "Request body is required.");

        Color color = ParseColor(request.Color);
        SeatKind kind = ParseKind(request.Kind, allowOpen: false);
        Room room = Require(id);
        GameStateDto state;
        long version;
        string token;
        lock (room.Gate)
        {
            Seat seat = color == Color.Red ? room.Red : room.Black;
            if (seat.Kind != SeatKind.Open)
                throw new ApiException(409, "Seat is already taken.");

            seat.Kind = kind;
            seat.Token = NewToken();
            token = seat.Token;
            room.LastActivity = DateTimeOffset.UtcNow;
            version = ++room.Version;
            state = ProjectLocked(room);
        }

        await PublishAsync(room, version);
        return new JoinResponseDto(token, ColorName(color), state);
    }

    /// <summary>
    /// Drops rooms that have been idle past the ongoing or finished limit. Returns how many were removed.
    /// </summary>
    public int SweepExpired(DateTimeOffset now)
    {
        int removed = 0;
        foreach (KeyValuePair<Guid, Room> entry in _rooms)
        {
            bool expired;
            lock (entry.Value.Gate)
                expired = IsExpiredLocked(entry.Value, now);

            if (expired && _rooms.TryRemove(entry.Key, out _))
                removed++;
        }

        return removed;
    }

    private bool IsExpiredLocked(Room room, DateTimeOffset now)
    {
        TimeSpan idle = now - room.LastActivity;
        if (room.Game.Status == GameStatus.Ongoing)
            return idle > TimeSpan.FromMinutes(_options.RoomIdleMinutes);
        return idle > TimeSpan.FromMinutes(_options.FinishedRoomMinutes);
    }

    private CreateRoomResponseDto AddRoom(string? requestedName, SeatKind redKind, SeatKind blackKind, Difficulty difficulty, bool isPublic)
    {
        if (_rooms.Count >= _options.MaxRooms)
            throw new ApiException(503, "房间已满，请稍后再试 / server is full, try again later");

        Guid id = Guid.NewGuid();
        var room = new Room(
            id,
            CleanName(requestedName, id),
            new Game(),
            MakeSeat(redKind, difficulty),
            MakeSeat(blackKind, difficulty),
            isPublic);

        if (!_rooms.TryAdd(room.Id, room))
            throw new ApiException(500, "Could not create a room.");

        GameStateDto state;
        lock (room.Gate)
        {
            room.LastActivity = DateTimeOffset.UtcNow;
            state = ProjectLocked(room);
        }

        ScheduleAi(room);
        return new CreateRoomResponseDto(
            room.Id,
            room.Name,
            new SeatTokensDto(room.Red.Token, room.Black.Token),
            state);
    }

    private void ScheduleAi(Room room)
    {
        Board snapshot;
        SearchLimits limits;
        Color color;
        lock (room.Gate)
        {
            if (room.AiRunning || room.Game.Status != GameStatus.Ongoing)
                return;

            color = room.Game.SideToMove;
            Seat seat = color == Color.Red ? room.Red : room.Black;
            if (seat.Kind != SeatKind.BuiltinAi)
                return;

            room.AiRunning = true;
            snapshot = room.Game.Board.Clone();
            limits = SearchLimits.For(seat.Difficulty);
        }

        _ = Task.Run(() => RunAiAsync(room, color, snapshot, limits));
    }

    private async Task RunAiAsync(Room room, Color color, Board snapshot, SearchLimits limits)
    {
        var handedOff = false;
        try
        {
            EngineResult result = await _engine.SearchAsync(snapshot, limits, _shutdown.Token);
            long version = 0;
            var played = false;
            lock (room.Gate)
            {
                room.AiRunning = false;
                if (room.Game.Status == GameStatus.Ongoing
                    && room.Game.SideToMove == color
                    && !result.BestMove.IsNone
                    && room.Game.Board.IsLegal(result.BestMove))
                {
                    room.Game.MakeMove(result.BestMove);
                    room.LastActivity = DateTimeOffset.UtcNow;
                    version = ++room.Version;
                    played = true;
                    _logger.LogInformation("AI played {Move} in room {RoomId}", result.BestMove, room.Id);
                }
                else if (room.Game.Status == GameStatus.Ongoing && room.Game.SideToMove == color)
                {
                    _logger.LogWarning("AI produced no legal move in room {RoomId}", room.Id);
                }
            }

            if (!played)
                return;

            // The room may already have been swept. Publishing a detached room must not throw.
            if (!_rooms.ContainsKey(room.Id))
                return;

            ScheduleAi(room);
            handedOff = true;
            await PublishAsync(room, version);
        }
        catch (OperationCanceledException)
        {
            // Host is stopping.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI search failed in room {RoomId}", room.Id);
        }
        finally
        {
            if (!handedOff)
            {
                lock (room.Gate)
                    room.AiRunning = false;
            }
        }
    }

    private async Task PublishAsync(Room room, long version)
    {
        await room.PublishLock.WaitAsync();
        try
        {
            GameStateDto state;
            lock (room.Gate)
            {
                // A newer publisher owns the broadcast when the room moved on.
                if (room.Version != version)
                    return;
                state = ProjectLocked(room);
            }

            await _hub.Clients.Group(GroupName(room.Id)).SendAsync("state", state);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to publish room {RoomId}", room.Id);
        }
        finally
        {
            room.PublishLock.Release();
        }
    }

    private Room Require(Guid id)
    {
        if (_rooms.TryGetValue(id, out Room? room))
            return room;
        throw new ApiException(404, "Room not found.");
    }

    private static GameStateDto Project(Room room)
    {
        lock (room.Gate)
            return ProjectLocked(room);
    }

    private static GameStateDto ProjectLocked(Room room)
    {
        Game game = room.Game;
        var history = new string[game.History.Count];
        for (int i = 0; i < history.Length; i++)
            history[i] = game.History[i].ToString();

        return new GameStateDto(
            room.Id,
            room.Name,
            game.Fen,
            BoardRows(game.Board),
            ColorName(game.SideToMove),
            StatusName(game.Status),
            game.Board.IsInCheck(),
            history.Length == 0 ? null : history[^1],
            history,
            ProjectSeats(room));
    }

    private static SeatsDto ProjectSeats(Room room) =>
        new(ProjectSeat(room.Red), ProjectSeat(room.Black));

    private static SeatDto ProjectSeat(Seat seat) =>
        new(
            KindName(seat.Kind),
            seat.Kind != SeatKind.Open,
            seat.Kind == SeatKind.BuiltinAi ? DifficultyName(seat.Difficulty) : null);

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

    private static Seat MakeSeat(SeatKind kind, Difficulty difficulty)
    {
        string? token = kind is SeatKind.Human or SeatKind.ExternalBot ? NewToken() : null;
        return new Seat(kind, difficulty, token);
    }

    private static bool TokenOwns(Seat seat, string? token)
    {
        if (seat.Kind is not (SeatKind.Human or SeatKind.ExternalBot))
            return false;
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrEmpty(seat.Token))
            return false;
        return string.Equals(seat.Token, token.Trim(), StringComparison.Ordinal);
    }

    private static string NewToken() => Guid.NewGuid().ToString("N");

    private static string CleanName(string? name, Guid id)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "擂台 " + id.ToString("N")[..6];

        string trimmed = name.Trim();
        return trimmed.Length <= 40 ? trimmed : trimmed[..40];
    }

    private static SeatKind ParseKind(string? text, bool allowOpen)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ApiException(400, "Seat kind is required.");

        switch (text.Trim().ToLowerInvariant())
        {
            case "human":
                return SeatKind.Human;
            case "ai":
            case "builtin":
            case "builtinai":
                return SeatKind.BuiltinAi;
            case "bot":
            case "external":
            case "externalbot":
                return SeatKind.ExternalBot;
            case "open":
            case "empty":
            case "none":
                if (allowOpen)
                    return SeatKind.Open;
                break;
        }

        throw new ApiException(400, allowOpen
            ? "Seat kind must be human, ai, bot, or open."
            : "Seat kind must be human or bot.");
    }

    private static Difficulty ParseDifficulty(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Difficulty.Medium;

        switch (text.Trim().ToLowerInvariant())
        {
            case "easy":
                return Difficulty.Easy;
            case "medium":
                return Difficulty.Medium;
            case "hard":
                return Difficulty.Hard;
            default:
                throw new ApiException(400, "Difficulty must be easy, medium, or hard.");
        }
    }

    private static bool ParseHumanSide(string? text)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "red":
                return true;
            case "black":
                return false;
            default:
                throw new ApiException(400, "humanColor must be red or black.");
        }
    }

    private static Color ParseColor(string? text)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "red":
                return Color.Red;
            case "black":
                return Color.Black;
            default:
                throw new ApiException(400, "Color must be red or black.");
        }
    }

    private static string ColorName(Color color) => color == Color.Red ? "red" : "black";

    private static string KindName(SeatKind kind) => kind switch
    {
        SeatKind.Human => "human",
        SeatKind.BuiltinAi => "ai",
        SeatKind.ExternalBot => "bot",
        _ => "open"
    };

    private static string DifficultyName(Difficulty difficulty) => difficulty switch
    {
        Difficulty.Easy => "easy",
        Difficulty.Hard => "hard",
        _ => "medium"
    };

    private static string DifficultyLabel(Difficulty difficulty) => difficulty switch
    {
        Difficulty.Easy => "简单",
        Difficulty.Hard => "困难",
        _ => "中等"
    };

    private static string StatusName(GameStatus status) => status switch
    {
        GameStatus.RedWins => "redWins",
        GameStatus.BlackWins => "blackWins",
        GameStatus.Draw => "draw",
        _ => "ongoing"
    };
}
