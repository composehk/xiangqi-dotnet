using Xiangqi.Core;

namespace Xiangqi.Engine;

/// <summary>
/// Negamax alpha-beta with iterative deepening, capture quiescence, MVV-LVA and
/// killer ordering, and depth plus time limits. Stalemate is scored as a loss.
/// Repetition draws inside the tree are threefold along the search path; the
/// engine does not see game history from before the searched position.
/// </summary>
public sealed class AlphaBetaEngine : IEngine
{
    private const int Infinity = 1_000_000;
    private const int MateScore = 100_000;
    private const int MateBound = 90_000;
    private const int MaxPly = 128;
    private const int MaxQPly = 8;
    private const int TtSize = 1 << 18;
    private const int TtMask = TtSize - 1;
    private const byte TtExact = 0;
    private const byte TtLower = 1;
    private const byte TtUpper = 2;

    private readonly Move[][] _lists;
    private readonly int[] _orderScores = new int[Board.MaxMoves];
    private readonly Move[,] _killers = new Move[MaxPly, 2];
    private readonly ulong[] _path = new ulong[MaxPly];
    private readonly TTEntry[] _tt = new TTEntry[TtSize];
    private readonly object _gate = new();

    private Board _board = null!;
    private CancellationToken _ct;
    private long _nodes;
    private long _t0;
    private int _timeMs;
    private bool _respectTime;
    private bool _abort;

    public AlphaBetaEngine()
    {
        _lists = new Move[MaxPly][];
        for (int ply = 0; ply < MaxPly; ply++)
            _lists[ply] = new Move[Board.MaxMoves];
    }

    public Task<EngineResult> SearchAsync(Board board, SearchLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(board);
        Board clone = board.Clone();
        return Task.Run(() =>
        {
            lock (_gate)
                return Search(clone, limits, cancellationToken);
        }, cancellationToken);
    }

    private EngineResult Search(Board board, SearchLimits limits, CancellationToken cancellationToken)
    {
        _board = board;
        _ct = cancellationToken;
        _nodes = 0;
        _abort = false;
        _t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _timeMs = limits.TimeMs < 0 ? 0 : limits.TimeMs;
        Array.Clear(_killers, 0, _killers.Length);
        Array.Clear(_tt, 0, _tt.Length);

        int moveCount = board.FillLegalMoves(_lists[0]);
        _nodes++;
        if (moveCount == 0)
            return new EngineResult(Move.None, -MateScore, 0, _nodes);

        _path[0] = board.Hash;
        Move bestMove = _lists[0][0];
        int bestScore = 0;
        int completedDepth = 0;
        int maxDepth = limits.Depth < 1 ? 1 : limits.Depth;

        for (int depth = 1; depth <= maxDepth; depth++)
        {
            _respectTime = depth > 1;
            _abort = false;
            if (_respectTime && (TimeUp() || _ct.IsCancellationRequested))
                break;

            Order(_lists[0], moveCount, 0, bestMove);
            int alpha = -Infinity;
            Move iterationBest = _lists[0][0];
            int iterationScore = -Infinity;

            for (int i = 0; i < moveCount; i++)
            {
                if (_respectTime && (TimeUp() || _ct.IsCancellationRequested))
                {
                    _abort = true;
                    break;
                }

                Move move = _lists[0][i];
                _board.MakeMove(move);
                int score = -Negamax(depth - 1, -Infinity, -alpha, 1);
                _board.UnmakeMove();
                if (_abort)
                    break;

                if (score > iterationScore)
                {
                    iterationScore = score;
                    iterationBest = move;
                }

                if (score > alpha)
                    alpha = score;
            }

            if (_abort)
                break;

            bestMove = iterationBest;
            bestScore = iterationScore;
            completedDepth = depth;
            if (Math.Abs(bestScore) >= MateBound)
                break;
        }

        _ct.ThrowIfCancellationRequested();
        return new EngineResult(bestMove, bestScore, completedDepth, _nodes);
    }

    private int Negamax(int depth, int alpha, int beta, int ply)
    {
        if (depth <= 0)
            return Quiescence(alpha, beta, ply, 0);

        _nodes++;
        if (StopRequested() || ply >= MaxPly)
            return StopRequested() ? 0 : Evaluation.Evaluate(_board);

        ulong hash = _board.Hash;
        if (IsThreefold(ply, hash))
            return 0;
        _path[ply] = hash;

        Move ttMove = Move.None;
        if (Probe(hash, depth, ply, alpha, beta, ref ttMove, out int ttScore))
            return ttScore;

        int count = _board.FillLegalMoves(_lists[ply]);
        if (count == 0)
            return -MateScore + ply;
        if (_board.HalfmoveClock >= 120)
            return 0;

        Order(_lists[ply], count, ply, ttMove);
        int alphaOriginal = alpha;
        int bestScore = -Infinity;
        Move bestMove = Move.None;

        for (int i = 0; i < count; i++)
        {
            if (_abort)
                return 0;

            Move move = _lists[ply][i];
            bool capture = _board.PieceAt(move.To) != Piece.None;
            _board.MakeMove(move);
            int score = -Negamax(depth - 1, -beta, -alpha, ply + 1);
            _board.UnmakeMove();
            if (_abort)
                return 0;

            if (score > bestScore)
            {
                bestScore = score;
                bestMove = move;
            }

            if (score > alpha)
                alpha = score;
            if (alpha >= beta)
            {
                if (!capture)
                    StoreKiller(ply, move);
                break;
            }
        }

        Store(hash, depth, ply, bestScore, alphaOriginal, beta, bestMove);
        return bestScore;
    }

    private int Quiescence(int alpha, int beta, int ply, int qply)
    {
        _nodes++;
        if (StopRequested())
            return 0;
        if (ply >= MaxPly || qply > MaxQPly)
            return Evaluation.Evaluate(_board);

        ulong hash = _board.Hash;
        if (IsThreefold(ply, hash))
            return 0;
        _path[ply] = hash;

        bool inCheck = _board.IsInCheck();
        int count = _board.FillLegalMoves(_lists[ply]);
        if (count == 0)
            return -MateScore + ply;
        if (_board.HalfmoveClock >= 120)
            return 0;

        int bestScore;
        if (inCheck)
        {
            bestScore = -Infinity;
        }
        else
        {
            int stand = Evaluation.Evaluate(_board);
            if (stand >= beta)
                return stand;
            if (stand > alpha)
                alpha = stand;
            bestScore = stand;
        }

        Order(_lists[ply], count, ply, Move.None);
        for (int i = 0; i < count; i++)
        {
            Move move = _lists[ply][i];
            bool capture = _board.PieceAt(move.To) != Piece.None;
            if (!inCheck && !capture)
                continue;
            if (_abort)
                return 0;

            _board.MakeMove(move);
            int score = -Quiescence(-beta, -alpha, ply + 1, qply + 1);
            _board.UnmakeMove();
            if (_abort)
                return 0;

            if (score > bestScore)
                bestScore = score;
            if (score > alpha)
                alpha = score;
            if (alpha >= beta)
                return score;
        }

        return bestScore;
    }

    private void Order(Move[] moves, int count, int ply, Move ttMove)
    {
        for (int i = 0; i < count; i++)
        {
            Move move = moves[i];
            int score;
            if (!ttMove.IsNone && move == ttMove)
            {
                score = 1_000_000;
            }
            else
            {
                Piece victim = _board.PieceAt(move.To);
                if (victim != Piece.None)
                {
                    Piece attacker = _board.PieceAt(move.From);
                    score = 100_000 + Evaluation.VictimValue(victim) * 16 - Evaluation.VictimValue(attacker);
                }
                else if (ply < MaxPly && move == _killers[ply, 0])
                {
                    score = 90_000;
                }
                else if (ply < MaxPly && move == _killers[ply, 1])
                {
                    score = 80_000;
                }
                else
                {
                    score = 0;
                }
            }

            _orderScores[i] = score;
        }

        for (int i = 1; i < count; i++)
        {
            Move move = moves[i];
            int score = _orderScores[i];
            int j = i - 1;
            while (j >= 0 && _orderScores[j] < score)
            {
                _orderScores[j + 1] = _orderScores[j];
                moves[j + 1] = moves[j];
                j--;
            }

            _orderScores[j + 1] = score;
            moves[j + 1] = move;
        }
    }

    private void StoreKiller(int ply, Move move)
    {
        if ((uint)ply >= MaxPly || _killers[ply, 0] == move)
            return;
        _killers[ply, 1] = _killers[ply, 0];
        _killers[ply, 0] = move;
    }

    private bool Probe(ulong key, int depth, int ply, int alpha, int beta, ref Move ttMove, out int score)
    {
        score = 0;
        ref TTEntry entry = ref _tt[(int)(key & TtMask)];
        if (!entry.Present || entry.Key != key)
            return false;

        ttMove = new Move(entry.From, entry.To);
        if (entry.Depth < depth)
            return false;

        int adjusted = FromTt(entry.Score, ply);
        if (entry.Flag == TtExact || (entry.Flag == TtLower && adjusted >= beta) || (entry.Flag == TtUpper && adjusted <= alpha))
        {
            score = adjusted;
            return true;
        }

        return false;
    }

    private void Store(ulong key, int depth, int ply, int score, int alphaOriginal, int beta, Move bestMove)
    {
        byte flag = score <= alphaOriginal ? TtUpper : score >= beta ? TtLower : TtExact;
        ref TTEntry entry = ref _tt[(int)(key & TtMask)];
        entry.Key = key;
        entry.Score = ToTt(score, ply);
        entry.Depth = (short)depth;
        entry.Flag = flag;
        entry.From = bestMove.From;
        entry.To = bestMove.To;
        entry.Present = true;
    }

    private bool IsThreefold(int ply, ulong hash)
    {
        int seen = 1;
        for (int i = 0; i < ply; i++)
        {
            if (_path[i] == hash && ++seen >= 3)
                return true;
        }

        return false;
    }

    private bool StopRequested()
    {
        if (_abort)
            return true;
        if ((_nodes & 2047) != 0)
            return false;
        if (_ct.IsCancellationRequested || (_respectTime && TimeUp()))
        {
            _abort = true;
            return true;
        }

        return false;
    }

    private bool TimeUp()
    {
        if (_timeMs <= 0)
            return false;
        long elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - _t0) * 1000
            / System.Diagnostics.Stopwatch.Frequency;
        return elapsed >= _timeMs;
    }

    private static int ToTt(int score, int ply)
    {
        if (score > MateBound)
            return score + ply;
        if (score < -MateBound)
            return score - ply;
        return score;
    }

    private static int FromTt(int score, int ply)
    {
        if (score > MateBound)
            return score - ply;
        if (score < -MateBound)
            return score + ply;
        return score;
    }

    private struct TTEntry
    {
        public ulong Key;
        public int Score;
        public short Depth;
        public byte Flag;
        public byte From;
        public byte To;
        public bool Present;
    }
}
