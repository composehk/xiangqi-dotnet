namespace Xiangqi.Core;

public enum GameStatus
{
    Ongoing,
    RedWins,
    BlackWins,
    Draw
}

/// <summary>
/// A played game. Checkmate and stalemate are both losses for the side to move.
/// The game is also drawn by threefold repetition of the same piece placement and
/// side to move, or when 120 plies pass without a capture.
/// </summary>
public sealed class Game
{
    private readonly Board _board;
    private readonly List<Move> _history = new();
    private readonly Dictionary<ulong, int> _repetitions = new();

    public Game()
        : this(Board.StartFen)
    {
    }

    public Game(string fen)
    {
        _board = Board.FromFen(fen);
        RecordRepetition();
        RefreshStatus();
    }

    public Board Board => _board;
    public GameStatus Status { get; private set; }
    public IReadOnlyList<Move> History => _history;
    public Color SideToMove => _board.SideToMove;
    public string Fen => _board.ToFen();

    public void MakeMove(string notation) => MakeMove(Move.Parse(notation));

    public void MakeMove(Move move)
    {
        if (Status != GameStatus.Ongoing)
            throw new InvalidOperationException("The game is already over.");
        if (!_board.IsLegal(move))
            throw new ArgumentException($"Illegal move {move}.", nameof(move));

        _board.MakeMove(move);
        _history.Add(move);
        RecordRepetition();
        RefreshStatus();
    }

    public void Resign(Color loser)
    {
        if (Status != GameStatus.Ongoing)
            throw new InvalidOperationException("The game is already over.");

        Status = loser == Color.Red ? GameStatus.BlackWins : GameStatus.RedWins;
    }

    private void RecordRepetition()
    {
        ulong hash = _board.Hash;
        _repetitions.TryGetValue(hash, out int count);
        _repetitions[hash] = count + 1;
    }

    private void RefreshStatus()
    {
        // A side with no legal move has lost, whether or not it is in check.
        // That takes priority over the 120-ply draw.
        if (!_board.HasLegalMove())
        {
            Status = _board.SideToMove == Color.Red ? GameStatus.BlackWins : GameStatus.RedWins;
            return;
        }

        _repetitions.TryGetValue(_board.Hash, out int repetitions);
        if (_board.HalfmoveClock >= 120 || repetitions >= 3)
        {
            Status = GameStatus.Draw;
            return;
        }

        Status = GameStatus.Ongoing;
    }
}
