using Xiangqi.Core;

namespace Xiangqi.Engine;

/// <summary>
/// Material plus piece-square tables. Positive scores favor the side to move.
/// </summary>
internal static class Evaluation
{
    private static readonly short[] Material = new short[8];
    private static readonly short[,,] Pst = new short[2, 8, Board.SquareCount];

    static Evaluation()
    {
        Material[(int)PieceType.Advisor] = 120;
        Material[(int)PieceType.Elephant] = 120;
        Material[(int)PieceType.Horse] = 450;
        Material[(int)PieceType.Cannon] = 480;
        Material[(int)PieceType.Chariot] = 1000;
        Material[(int)PieceType.Soldier] = 80;

        var red = new short[8, Board.SquareCount];
        FillSoldiers(red);
        FillLeapers(red, PieceType.Horse, new short[] { 0, 8, 18, 28, 36, 40, 36, 28, 18, 8 }, new short[] { -10, 0, 12, 22, 28, 22, 12, 0, -10 });
        FillLeapers(red, PieceType.Cannon, new short[] { 0, 4, 14, 22, 26, 26, 22, 16, 10, 6 }, new short[] { -4, 8, 14, 18, 18, 18, 14, 8, -4 });
        FillLeapers(red, PieceType.Chariot, new short[] { 0, 6, 12, 20, 32, 44, 58, 72, 84, 90 }, new short[] { 4, 10, 16, 20, 24, 20, 16, 10, 4 });
        Set(red, PieceType.Elephant, 2, 0, 12);
        Set(red, PieceType.Elephant, 6, 0, 12);
        Set(red, PieceType.Elephant, 0, 2, 8);
        Set(red, PieceType.Elephant, 4, 2, 16);
        Set(red, PieceType.Elephant, 8, 2, 8);
        Set(red, PieceType.Elephant, 2, 4, 20);
        Set(red, PieceType.Elephant, 6, 4, 20);
        Set(red, PieceType.Advisor, 3, 0, 12);
        Set(red, PieceType.Advisor, 5, 0, 12);
        Set(red, PieceType.Advisor, 4, 1, 16);
        Set(red, PieceType.Advisor, 3, 2, 8);
        Set(red, PieceType.Advisor, 5, 2, 8);
        Set(red, PieceType.General, 3, 0, 20);
        Set(red, PieceType.General, 4, 0, 30);
        Set(red, PieceType.General, 5, 0, 20);
        Set(red, PieceType.General, 3, 1, 4);
        Set(red, PieceType.General, 4, 1, 8);
        Set(red, PieceType.General, 5, 1, 4);
        Set(red, PieceType.General, 3, 2, -12);
        Set(red, PieceType.General, 4, 2, -8);
        Set(red, PieceType.General, 5, 2, -12);

        for (int type = 1; type < 8; type++)
        {
            for (int square = 0; square < Board.SquareCount; square++)
            {
                Pst[0, type, square] = red[type, square];
                Pst[1, type, square] = red[type, Mirror(square)];
            }
        }
    }

    public static int Evaluate(Board board)
    {
        int score = 0;
        for (int square = 0; square < Board.SquareCount; square++)
        {
            Piece piece = board.PieceAt(square);
            if (piece == Piece.None)
                continue;

            int type = (int)piece.GetPieceType();
            int color = (int)piece.GetColor();
            int value = Material[type] + Pst[color, type, square];
            score += color == (int)Color.Red ? value : -value;
        }

        return board.SideToMove == Color.Red ? score : -score;
    }

    public static int VictimValue(Piece piece) => piece == Piece.None ? 0 : Material[(int)piece.GetPieceType()];

    private static void FillSoldiers(short[,] red)
    {
        short[] advance = { 0, 0, 0, 0, 15, 50, 80, 110, 140, 150 };
        short[] fileBonus = { 0, 4, 8, 16, 20, 16, 8, 4, 0 };
        for (int rank = 0; rank < Board.RankCount; rank++)
        {
            for (int file = 0; file < Board.FileCount; file++)
                red[(int)PieceType.Soldier, rank * Board.FileCount + file] = (short)(advance[rank] + fileBonus[file]);
        }
    }

    private static void FillLeapers(short[,] red, PieceType type, short[] rankBonus, short[] fileBonus)
    {
        for (int rank = 0; rank < Board.RankCount; rank++)
        {
            for (int file = 0; file < Board.FileCount; file++)
                red[(int)type, rank * Board.FileCount + file] = (short)(rankBonus[rank] + fileBonus[file]);
        }
    }

    private static void Set(short[,] red, PieceType type, int file, int rank, short value)
        => red[(int)type, rank * Board.FileCount + file] = value;

    private static int Mirror(int square)
    {
        int rank = square / Board.FileCount;
        int file = square % Board.FileCount;
        return (Board.RankCount - 1 - rank) * Board.FileCount + file;
    }
}
