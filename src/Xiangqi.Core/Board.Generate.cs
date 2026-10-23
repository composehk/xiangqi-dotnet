using System.Runtime.CompilerServices;

namespace Xiangqi.Core;

public sealed partial class Board
{
    // Horse deltas and the orthogonal leg that blocks each one.
    private static readonly sbyte[] HorseFile = { 1, 1, -1, -1, 2, 2, -2, -2 };
    private static readonly sbyte[] HorseRank = { 2, -2, 2, -2, 1, -1, 1, -1 };
    private static readonly sbyte[] HorseLegFile = { 0, 0, 0, 0, 1, 1, -1, -1 };
    private static readonly sbyte[] HorseLegRank = { 1, -1, 1, -1, 0, 0, 0, 0 };

    private static readonly sbyte[] ElephantFile = { 2, 2, -2, -2 };
    private static readonly sbyte[] ElephantRank = { 2, -2, 2, -2 };

    private static readonly sbyte[] OrthFile = { 1, -1, 0, 0 };
    private static readonly sbyte[] OrthRank = { 0, 0, 1, -1 };
    private static readonly sbyte[] DiagFile = { 1, 1, -1, -1 };
    private static readonly sbyte[] DiagRank = { 1, -1, 1, -1 };

    internal int FillLegalMoves(Span<Move> destination)
    {
        int pseudoCount = FillPseudoLegalMoves(_pseudo);
        int count = 0;
        for (int i = 0; i < pseudoCount; i++)
        {
            Move move = _pseudo[i];
            MakeMove(move);
            // Side has switched, so the mover is the other color.
            bool illegal = IsInCheck(_sideToMove.Opposite());
            UnmakeMove();
            if (illegal)
                continue;
            if (count >= destination.Length)
                throw new InvalidOperationException("Legal move buffer overflow.");
            destination[count++] = move;
        }

        return count;
    }

    private int FillPseudoLegalMoves(Span<Move> destination)
    {
        int count = 0;
        Color side = _sideToMove;
        for (int square = 0; square < SquareCount; square++)
        {
            Piece piece = _squares[square];
            if (piece == Piece.None || piece.GetColor() != side)
                continue;

            count = piece.GetPieceType() switch
            {
                PieceType.General => GenerateGeneral(square, destination, count),
                PieceType.Advisor => GenerateAdvisor(square, destination, count),
                PieceType.Elephant => GenerateElephant(square, destination, count),
                PieceType.Horse => GenerateHorse(square, destination, count),
                PieceType.Chariot => GenerateChariot(square, destination, count),
                PieceType.Cannon => GenerateCannon(square, destination, count),
                PieceType.Soldier => GenerateSoldier(square, destination, count),
                _ => count
            };
        }

        return count;
    }

    public bool IsSquareAttacked(int target, Color byColor)
    {
        int file = target % FileCount;
        int rank = target / FileCount;

        if (AttackedBySoldier(file, rank, byColor)
            || AttackedByHorse(file, rank, byColor)
            || AttackedByElephant(file, rank, byColor)
            || AttackedByAdvisor(file, rank, byColor)
            || AttackedByGeneralStep(file, rank, byColor)
            || AttackedByRider(file, rank, byColor))
            return true;

        // Flying general: the enemy general attacks this general along an open file.
        int defenderKing = byColor == Color.Red ? _blackKing : _redKing;
        return target == defenderKing && KingsFace();
    }

    private bool AttackedBySoldier(int file, int rank, Color byColor)
    {
        if (byColor == Color.Red)
        {
            if (rank > 0 && _squares[(rank - 1) * FileCount + file] == Piece.RedSoldier)
                return true;
            if (rank >= 5)
            {
                if (file > 0 && _squares[rank * FileCount + file - 1] == Piece.RedSoldier)
                    return true;
                if (file < 8 && _squares[rank * FileCount + file + 1] == Piece.RedSoldier)
                    return true;
            }
        }
        else
        {
            if (rank < 9 && _squares[(rank + 1) * FileCount + file] == Piece.BlackSoldier)
                return true;
            if (rank <= 4)
            {
                if (file > 0 && _squares[rank * FileCount + file - 1] == Piece.BlackSoldier)
                    return true;
                if (file < 8 && _squares[rank * FileCount + file + 1] == Piece.BlackSoldier)
                    return true;
            }
        }

        return false;
    }

    private bool AttackedByHorse(int file, int rank, Color byColor)
    {
        Piece horse = byColor == Color.Red ? Piece.RedHorse : Piece.BlackHorse;
        for (int i = 0; i < 8; i++)
        {
            int horseFile = file - HorseFile[i];
            int horseRank = rank - HorseRank[i];
            if ((uint)horseFile >= FileCount || (uint)horseRank >= RankCount)
                continue;
            if (_squares[horseRank * FileCount + horseFile] != horse)
                continue;

            int legFile = horseFile + HorseLegFile[i];
            int legRank = horseRank + HorseLegRank[i];
            if ((uint)legFile >= FileCount || (uint)legRank >= RankCount)
                continue;
            if (_squares[legRank * FileCount + legFile] == Piece.None)
                return true;
        }

        return false;
    }

    private bool AttackedByElephant(int file, int rank, Color byColor)
    {
        Piece elephant = byColor == Color.Red ? Piece.RedElephant : Piece.BlackElephant;
        bool red = byColor == Color.Red;
        for (int i = 0; i < 4; i++)
        {
            int elephantFile = file - ElephantFile[i];
            int elephantRank = rank - ElephantRank[i];
            if ((uint)elephantFile >= FileCount || (uint)elephantRank >= RankCount)
                continue;
            if (_squares[elephantRank * FileCount + elephantFile] != elephant)
                continue;
            // Neither the elephant nor the target may stand across the river.
            if (red)
            {
                if (elephantRank > 4 || rank > 4)
                    continue;
            }
            else if (elephantRank < 5 || rank < 5)
            {
                continue;
            }

            int eye = (rank - ElephantRank[i] / 2) * FileCount + file - ElephantFile[i] / 2;
            if (_squares[eye] == Piece.None)
                return true;
        }

        return false;
    }

    private bool AttackedByAdvisor(int file, int rank, Color byColor)
    {
        if (!InPalace(file, rank, byColor))
            return false;

        Piece advisor = byColor == Color.Red ? Piece.RedAdvisor : Piece.BlackAdvisor;
        for (int i = 0; i < 4; i++)
        {
            int advisorFile = file - DiagFile[i];
            int advisorRank = rank - DiagRank[i];
            if (!InPalace(advisorFile, advisorRank, byColor))
                continue;
            if (_squares[advisorRank * FileCount + advisorFile] == advisor)
                return true;
        }

        return false;
    }

    private bool AttackedByGeneralStep(int file, int rank, Color byColor)
    {
        if (!InPalace(file, rank, byColor))
            return false;

        Piece general = byColor == Color.Red ? Piece.RedGeneral : Piece.BlackGeneral;
        for (int i = 0; i < 4; i++)
        {
            int generalFile = file - OrthFile[i];
            int generalRank = rank - OrthRank[i];
            if (!InPalace(generalFile, generalRank, byColor))
                continue;
            if (_squares[generalRank * FileCount + generalFile] == general)
                return true;
        }

        return false;
    }

    private bool AttackedByRider(int file, int rank, Color byColor)
    {
        Piece chariot = byColor == Color.Red ? Piece.RedChariot : Piece.BlackChariot;
        Piece cannon = byColor == Color.Red ? Piece.RedCannon : Piece.BlackCannon;
        for (int d = 0; d < 4; d++)
        {
            int df = OrthFile[d];
            int dr = OrthRank[d];
            int f = file + df;
            int r = rank + dr;
            while ((uint)f < FileCount && (uint)r < RankCount)
            {
                Piece piece = _squares[r * FileCount + f];
                if (piece != Piece.None)
                {
                    if (piece == chariot)
                        return true;

                    f += df;
                    r += dr;
                    while ((uint)f < FileCount && (uint)r < RankCount)
                    {
                        Piece next = _squares[r * FileCount + f];
                        if (next != Piece.None)
                        {
                            if (next == cannon)
                                return true;
                            break;
                        }

                        f += df;
                        r += dr;
                    }

                    break;
                }

                f += df;
                r += dr;
            }
        }

        return false;
    }

    private bool KingsFace()
    {
        if (_redKing < 0 || _blackKing < 0 || _redKing == _blackKing)
            return false;

        int redFile = _redKing % FileCount;
        int blackFile = _blackKing % FileCount;
        if (redFile != blackFile)
            return false;

        int redRank = _redKing / FileCount;
        int blackRank = _blackKing / FileCount;
        int step = redRank < blackRank ? 1 : -1;
        for (int rank = redRank + step; rank != blackRank; rank += step)
        {
            if (_squares[rank * FileCount + redFile] != Piece.None)
                return false;
        }

        return true;
    }

    private int GenerateGeneral(int square, Span<Move> destination, int count)
    {
        int file = square % FileCount;
        int rank = square / FileCount;
        Color side = _sideToMove;
        for (int d = 0; d < 4; d++)
        {
            int f = file + OrthFile[d];
            int r = rank + OrthRank[d];
            if (InPalace(f, r, side))
                count = AddCaptureOrEmpty(square, f, r, destination, count);
        }

        return count;
    }

    private int GenerateAdvisor(int square, Span<Move> destination, int count)
    {
        int file = square % FileCount;
        int rank = square / FileCount;
        Color side = _sideToMove;
        for (int d = 0; d < 4; d++)
        {
            int f = file + DiagFile[d];
            int r = rank + DiagRank[d];
            if (InPalace(f, r, side))
                count = AddCaptureOrEmpty(square, f, r, destination, count);
        }

        return count;
    }

    private int GenerateElephant(int square, Span<Move> destination, int count)
    {
        int file = square % FileCount;
        int rank = square / FileCount;
        bool red = _sideToMove == Color.Red;
        // An elephant already across the river cannot move.
        if (red ? rank > 4 : rank < 5)
            return count;

        for (int d = 0; d < 4; d++)
        {
            int f = file + ElephantFile[d];
            int r = rank + ElephantRank[d];
            if ((uint)f >= FileCount || (uint)r >= RankCount)
                continue;
            if (red ? r > 4 : r < 5)
                continue;

            int eye = (rank + ElephantRank[d] / 2) * FileCount + file + ElephantFile[d] / 2;
            if (_squares[eye] != Piece.None)
                continue;
            count = AddCaptureOrEmpty(square, f, r, destination, count);
        }

        return count;
    }

    private int GenerateHorse(int square, Span<Move> destination, int count)
    {
        int file = square % FileCount;
        int rank = square / FileCount;
        for (int d = 0; d < 8; d++)
        {
            int f = file + HorseFile[d];
            int r = rank + HorseRank[d];
            if ((uint)f >= FileCount || (uint)r >= RankCount)
                continue;

            int legFile = file + HorseLegFile[d];
            int legRank = rank + HorseLegRank[d];
            if ((uint)legFile >= FileCount || (uint)legRank >= RankCount)
                continue;
            if (_squares[legRank * FileCount + legFile] != Piece.None)
                continue;
            count = AddCaptureOrEmpty(square, f, r, destination, count);
        }

        return count;
    }

    private int GenerateChariot(int square, Span<Move> destination, int count)
    {
        int file = square % FileCount;
        int rank = square / FileCount;
        for (int d = 0; d < 4; d++)
        {
            int f = file + OrthFile[d];
            int r = rank + OrthRank[d];
            while ((uint)f < FileCount && (uint)r < RankCount)
            {
                Piece piece = _squares[r * FileCount + f];
                if (piece == Piece.None)
                {
                    count = AddMove(square, r * FileCount + f, destination, count);
                }
                else
                {
                    if (piece.GetColor() != _sideToMove)
                        count = AddMove(square, r * FileCount + f, destination, count);
                    break;
                }

                f += OrthFile[d];
                r += OrthRank[d];
            }
        }

        return count;
    }

    private int GenerateCannon(int square, Span<Move> destination, int count)
    {
        int file = square % FileCount;
        int rank = square / FileCount;
        for (int d = 0; d < 4; d++)
        {
            int df = OrthFile[d];
            int dr = OrthRank[d];
            int f = file + df;
            int r = rank + dr;

            while ((uint)f < FileCount && (uint)r < RankCount)
            {
                if (_squares[r * FileCount + f] != Piece.None)
                    break;
                count = AddMove(square, r * FileCount + f, destination, count);
                f += df;
                r += dr;
            }

            if ((uint)f >= FileCount || (uint)r >= RankCount)
                continue;

            // Exactly one screen, then the next piece may be captured.
            f += df;
            r += dr;
            while ((uint)f < FileCount && (uint)r < RankCount)
            {
                Piece piece = _squares[r * FileCount + f];
                if (piece != Piece.None)
                {
                    if (piece.GetColor() != _sideToMove)
                        count = AddMove(square, r * FileCount + f, destination, count);
                    break;
                }

                f += df;
                r += dr;
            }
        }

        return count;
    }

    private int GenerateSoldier(int square, Span<Move> destination, int count)
    {
        int file = square % FileCount;
        int rank = square / FileCount;
        bool red = _sideToMove == Color.Red;
        int forwardRank = rank + (red ? 1 : -1);
        if ((uint)forwardRank < RankCount)
            count = AddCaptureOrEmpty(square, file, forwardRank, destination, count);

        // Sideways only after crossing the river, and never backward.
        bool crossed = red ? rank >= 5 : rank <= 4;
        if (!crossed)
            return count;

        if (file > 0)
            count = AddCaptureOrEmpty(square, file - 1, rank, destination, count);
        if (file < 8)
            count = AddCaptureOrEmpty(square, file + 1, rank, destination, count);
        return count;
    }

    private int AddCaptureOrEmpty(int from, int file, int rank, Span<Move> destination, int count)
    {
        int to = rank * FileCount + file;
        Piece piece = _squares[to];
        if (piece != Piece.None && piece.GetColor() == _sideToMove)
            return count;
        return AddMove(from, to, destination, count);
    }

    private static int AddMove(int from, int to, Span<Move> destination, int count)
    {
        if (count >= destination.Length)
            throw new InvalidOperationException("Move buffer overflow.");
        destination[count] = Move.FromSquares(from, to);
        return count + 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool InPalace(int file, int rank, Color color)
    {
        if ((uint)file >= FileCount || (uint)rank >= RankCount)
            return false;
        if (file < 3 || file > 5)
            return false;
        return color == Color.Red ? rank <= 2 : rank >= 7;
    }
}
