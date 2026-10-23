using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Xiangqi.Core;

/// <summary>
/// 9×10 Xiangqi board. File 0 is a and rank 0 is Red's back rank, so Red occupies
/// ranks 0–4 and the river lies between ranks 4 and 5. FEN rank 0 is the top of
/// the board (Black's back rank).
/// </summary>
public sealed partial class Board
{
    public const int FileCount = 9;
    public const int RankCount = 10;
    public const int SquareCount = 90;
    public const int MaxMoves = 256;

    public const string StartFen =
        "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    private readonly Piece[] _squares = new Piece[SquareCount];
    private readonly Move[] _pseudo = new Move[MaxMoves];
    private Undo[] _undos = new Undo[256];
    private int _undoCount;
    private Color _sideToMove;
    private int _halfmoveClock;
    private int _fullmoveNumber;
    private int _redKing = -1;
    private int _blackKing = -1;
    private ulong _hash;

    private static readonly ulong[] ZobristPiece = new ulong[15 * SquareCount];
    private static readonly ulong ZobristSide;

    static Board()
    {
        ulong state = 0xC0FFEE_51471A11UL;
        for (int i = 0; i < ZobristPiece.Length; i++)
            ZobristPiece[i] = SplitMix(ref state);
        ZobristSide = SplitMix(ref state);
    }

    private Board()
    {
    }

    public static Board FromFen(string fen)
    {
        if (string.IsNullOrWhiteSpace(fen))
            throw new ArgumentException("FEN is empty.", nameof(fen));

        var board = new Board();
        board.ParseFen(fen.Trim());
        board.RecomputeHash();
        board.AssertInvariants();
        return board;
    }

    public Color SideToMove => _sideToMove;
    public int HalfmoveClock => _halfmoveClock;
    public int FullmoveNumber => _fullmoveNumber;
    public ulong Hash => _hash;
    public int RedKingSquare => _redKing;
    public int BlackKingSquare => _blackKing;

    public Piece GetPiece(int square)
    {
        if ((uint)square >= SquareCount)
            throw new ArgumentOutOfRangeException(nameof(square));
        return _squares[square];
    }

    public Piece GetPiece(int file, int rank)
    {
        if ((uint)file >= FileCount)
            throw new ArgumentOutOfRangeException(nameof(file));
        if ((uint)rank >= RankCount)
            throw new ArgumentOutOfRangeException(nameof(rank));
        return _squares[rank * FileCount + file];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Piece PieceAt(int square) => _squares[square];

    public static int Square(int file, int rank) => rank * FileCount + file;

    public Board Clone()
    {
        var copy = new Board
        {
            _sideToMove = _sideToMove,
            _halfmoveClock = _halfmoveClock,
            _fullmoveNumber = _fullmoveNumber,
            _redKing = _redKing,
            _blackKing = _blackKing,
            _hash = _hash
        };
        Array.Copy(_squares, copy._squares, SquareCount);
        return copy;
    }

    public string ToFen()
    {
        var sb = new StringBuilder(80);
        for (int rank = RankCount - 1; rank >= 0; rank--)
        {
            int empty = 0;
            for (int file = 0; file < FileCount; file++)
            {
                Piece piece = _squares[rank * FileCount + file];
                if (piece == Piece.None)
                {
                    empty++;
                    continue;
                }

                if (empty > 0)
                {
                    sb.Append((char)('0' + empty));
                    empty = 0;
                }

                sb.Append(piece.ToFenChar());
            }

            if (empty > 0)
                sb.Append((char)('0' + empty));
            if (rank > 0)
                sb.Append('/');
        }

        sb.Append(_sideToMove == Color.Red ? " w " : " b ");
        sb.Append("- - ");
        sb.Append(_halfmoveClock);
        sb.Append(' ');
        sb.Append(_fullmoveNumber);
        return sb.ToString();
    }

    public void MakeMove(Move move)
    {
        if ((uint)move.From >= SquareCount || (uint)move.To >= SquareCount)
            throw new ArgumentOutOfRangeException(nameof(move));

        Piece moving = _squares[move.From];
        if (moving == Piece.None)
            throw new ArgumentException("Origin square is empty.", nameof(move));

        Piece captured = _squares[move.To];
        if (_undoCount == _undos.Length)
            Array.Resize(ref _undos, _undos.Length * 2);

        _undos[_undoCount++] = new Undo
        {
            Move = move,
            Captured = captured,
            Halfmove = _halfmoveClock,
            Fullmove = _fullmoveNumber,
            Side = _sideToMove,
            RedKing = _redKing,
            BlackKing = _blackKing,
            Hash = _hash
        };

        _squares[move.From] = Piece.None;
        _squares[move.To] = moving;

        if (moving == Piece.RedGeneral)
            _redKing = move.To;
        else if (moving == Piece.BlackGeneral)
            _blackKing = move.To;

        if (captured == Piece.RedGeneral)
            _redKing = -1;
        else if (captured == Piece.BlackGeneral)
            _blackKing = -1;

        _hash ^= ZobristPiece[(int)moving * SquareCount + move.From];
        _hash ^= ZobristPiece[(int)moving * SquareCount + move.To];
        if (captured != Piece.None)
            _hash ^= ZobristPiece[(int)captured * SquareCount + move.To];
        _hash ^= ZobristSide;

        // Only a capture resets the no-capture clock. Soldier moves do not.
        _halfmoveClock = captured == Piece.None ? _halfmoveClock + 1 : 0;
        if (_sideToMove == Color.Black)
            _fullmoveNumber++;
        _sideToMove = _sideToMove.Opposite();
        AssertInvariants();
    }

    public void UnmakeMove()
    {
        if (_undoCount == 0)
            throw new InvalidOperationException("No move to unmake.");

        Undo undo = _undos[--_undoCount];
        Piece moving = _squares[undo.Move.To];
        _squares[undo.Move.From] = moving;
        _squares[undo.Move.To] = undo.Captured;
        _sideToMove = undo.Side;
        _halfmoveClock = undo.Halfmove;
        _fullmoveNumber = undo.Fullmove;
        _redKing = undo.RedKing;
        _blackKing = undo.BlackKing;
        _hash = undo.Hash;
        AssertInvariants();
    }

    public bool IsInCheck() => IsInCheck(_sideToMove);

    public bool IsInCheck(Color color)
    {
        int king = color == Color.Red ? _redKing : _blackKing;
        return king >= 0 && IsSquareAttacked(king, color.Opposite());
    }

    public List<Move> GenerateLegalMoves()
    {
        Span<Move> buffer = stackalloc Move[MaxMoves];
        int count = FillLegalMoves(buffer);
        var list = new List<Move>(count);
        for (int i = 0; i < count; i++)
            list.Add(buffer[i]);
        return list;
    }

    public List<Move> GeneratePseudoLegalMoves()
    {
        Span<Move> buffer = stackalloc Move[MaxMoves];
        int count = FillPseudoLegalMoves(buffer);
        var list = new List<Move>(count);
        for (int i = 0; i < count; i++)
            list.Add(buffer[i]);
        return list;
    }

    public bool IsLegal(Move move)
    {
        if ((uint)move.From >= SquareCount || (uint)move.To >= SquareCount)
            return false;

        Span<Move> buffer = stackalloc Move[MaxMoves];
        int count = FillLegalMoves(buffer);
        for (int i = 0; i < count; i++)
        {
            if (buffer[i] == move)
                return true;
        }

        return false;
    }

    public bool HasLegalMove()
    {
        int count = FillPseudoLegalMoves(_pseudo);
        for (int i = 0; i < count; i++)
        {
            Move move = _pseudo[i];
            MakeMove(move);
            bool leavesKingInCheck = IsInCheck(_sideToMove.Opposite());
            UnmakeMove();
            if (!leavesKingInCheck)
                return true;
        }

        return false;
    }

    public long Perft(int depth)
    {
        if (depth < 0)
            throw new ArgumentOutOfRangeException(nameof(depth));
        if (depth == 0)
            return 1;

        var buffers = new Move[depth][];
        for (int i = 0; i < depth; i++)
            buffers[i] = new Move[MaxMoves];
        return Perft(depth, 0, buffers);
    }

    public List<(Move Move, long Nodes)> PerftDivide(int depth)
    {
        if (depth < 1)
            throw new ArgumentOutOfRangeException(nameof(depth));

        var moves = GenerateLegalMoves();
        var result = new List<(Move, long)>(moves.Count);
        foreach (Move move in moves)
        {
            MakeMove(move);
            long nodes = depth == 1 ? 1 : Perft(depth - 1);
            UnmakeMove();
            result.Add((move, nodes));
        }

        return result;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        for (int rank = RankCount - 1; rank >= 0; rank--)
        {
            sb.Append(rank).Append(' ');
            for (int file = 0; file < FileCount; file++)
            {
                Piece piece = _squares[rank * FileCount + file];
                sb.Append(piece == Piece.None ? '.' : piece.ToFenChar()).Append(' ');
            }

            sb.AppendLine();
        }

        sb.Append("  a b c d e f g h i  ");
        sb.Append(_sideToMove == Color.Red ? "w" : "b");
        sb.Append(" hm=").Append(_halfmoveClock);
        return sb.ToString();
    }

    private long Perft(int depth, int ply, Move[][] buffers)
    {
        int count = FillLegalMoves(buffers[ply]);
        if (depth == 1)
            return count;

        long total = 0;
        Move[] moves = buffers[ply];
        for (int i = 0; i < count; i++)
        {
            MakeMove(moves[i]);
            total += Perft(depth - 1, ply + 1, buffers);
            UnmakeMove();
        }

        return total;
    }

    private void ParseFen(string fen)
    {
        string[] parts = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new FormatException("FEN is empty.");

        string[] ranks = parts[0].Split('/');
        if (ranks.Length != RankCount)
            throw new FormatException($"Xiangqi FEN must have {RankCount} ranks.");

        Array.Clear(_squares, 0, SquareCount);
        _redKing = -1;
        _blackKing = -1;

        for (int fenRank = 0; fenRank < RankCount; fenRank++)
        {
            int rank = RankCount - 1 - fenRank;
            int file = 0;
            foreach (char c in ranks[fenRank])
            {
                if (c is >= '1' and <= '9')
                {
                    file += c - '0';
                    continue;
                }

                if (file >= FileCount)
                    throw new FormatException($"Rank {rank} exceeds {FileCount} files.");

                Piece piece = PieceExtensions.FromFenChar(c);
                int square = rank * FileCount + file;
                _squares[square] = piece;
                if (piece == Piece.RedGeneral)
                    _redKing = square;
                else if (piece == Piece.BlackGeneral)
                    _blackKing = square;
                file++;
            }

            if (file != FileCount)
                throw new FormatException($"Rank {rank} has {file} files instead of {FileCount}.");
        }

        if (parts.Length < 2)
            throw new FormatException("FEN is missing the side to move.");

        _sideToMove = parts[1] switch
        {
            "w" or "W" or "r" or "R" => Color.Red,
            "b" or "B" => Color.Black,
            _ => throw new FormatException($"Unknown side to move '{parts[1]}'.")
        };

        _halfmoveClock = parts.Length > 4 ? int.Parse(parts[4]) : 0;
        _fullmoveNumber = parts.Length > 5 ? int.Parse(parts[5]) : 1;
        if (_halfmoveClock < 0 || _fullmoveNumber < 1)
            throw new FormatException("Move clocks in the FEN are invalid.");
    }

    private void RecomputeHash()
    {
        ulong hash = 0;
        for (int square = 0; square < SquareCount; square++)
        {
            Piece piece = _squares[square];
            if (piece != Piece.None)
                hash ^= ZobristPiece[(int)piece * SquareCount + square];
        }

        if (_sideToMove == Color.Black)
            hash ^= ZobristSide;
        _hash = hash;
    }

    [Conditional("DEBUG")]
    private void AssertInvariants()
    {
        int red = -1;
        int black = -1;
        for (int square = 0; square < SquareCount; square++)
        {
            if (_squares[square] == Piece.RedGeneral)
                red = square;
            else if (_squares[square] == Piece.BlackGeneral)
                black = square;
        }

        if (red != _redKing || black != _blackKing)
            throw new InvalidOperationException("King square cache does not match the board.");

        ulong saved = _hash;
        RecomputeHash();
        if (saved != _hash)
            throw new InvalidOperationException("Incremental Zobrist hash does not match the board.");
    }

    private static ulong SplitMix(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        ulong z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private struct Undo
    {
        public Move Move;
        public Piece Captured;
        public int Halfmove;
        public int Fullmove;
        public Color Side;
        public int RedKing;
        public int BlackKing;
        public ulong Hash;
    }
}
