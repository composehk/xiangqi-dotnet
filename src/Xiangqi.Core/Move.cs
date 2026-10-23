namespace Xiangqi.Core;

/// <summary>
/// A move in file-rank notation such as "h2e2". Files are a–i and ranks are 0–9
/// counted from Red's back rank.
/// </summary>
public readonly record struct Move(byte From, byte To)
{
    public static Move None => new(255, 255);

    public bool IsNone => From == 255;

    public int FromFile => From % Board.FileCount;
    public int FromRank => From / Board.FileCount;
    public int ToFile => To % Board.FileCount;
    public int ToRank => To / Board.FileCount;

    public static Move FromSquares(int from, int to) => new((byte)from, (byte)to);

    public override string ToString()
    {
        if (IsNone)
            return "(none)";

        Span<char> text = stackalloc char[4];
        text[0] = (char)('a' + FromFile);
        text[1] = (char)('0' + FromRank);
        text[2] = (char)('a' + ToFile);
        text[3] = (char)('0' + ToRank);
        return new string(text);
    }

    public static Move Parse(string notation)
    {
        if (notation == null || notation.Length != 4)
            throw new FormatException($"Invalid move notation '{notation}'.");

        int fromFile = ParseFile(notation[0]);
        int fromRank = ParseRank(notation[1]);
        int toFile = ParseFile(notation[2]);
        int toRank = ParseRank(notation[3]);
        return new Move(
            (byte)(fromRank * Board.FileCount + fromFile),
            (byte)(toRank * Board.FileCount + toFile));
    }

    private static int ParseFile(char c)
    {
        if (c is >= 'a' and <= 'i')
            return c - 'a';
        if (c is >= 'A' and <= 'I')
            return c - 'A';
        throw new FormatException($"Invalid file '{c}'.");
    }

    private static int ParseRank(char c)
    {
        if (c is >= '0' and <= '9')
            return c - '0';
        throw new FormatException($"Invalid rank '{c}'.");
    }
}
