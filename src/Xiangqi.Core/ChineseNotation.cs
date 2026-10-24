using System.Text;

namespace Xiangqi.Core;

/// <summary>
/// Chinese move notation (中文记谱), e.g. 炮二平五 / 马8进7 / 前车进二.
/// Red files use Chinese numerals counted right-to-left from Red's side (一 = file i);
/// Black files use Arabic digits counted right-to-left from Black's side (1 = file a).
/// </summary>
public static class ChineseNotation
{
    private const string RedNums = "一二三四五六七八九";
    private const string BlackNums = "123456789";
    private const string FullWidth = "１２３４５６７８９";

    public static string Format(Board board, Move move)
    {
        var (canonical, _) = Describe(board, move);
        return canonical;
    }

    public static Move Parse(Board board, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new FormatException("Empty Chinese move.");
        Color side = board.SideToMove;
        string wanted = Normalize(text, side);
        var legal = board.GenerateLegalMoves();
        foreach (var m in legal)
            if (Describe(board, m).Canonical == wanted)
                return m;
        // Fallback: plain "piece + file" form even when doubled pieces would normally need 前/后.
        Move found = Move.None;
        int hits = 0;
        foreach (var m in legal)
        {
            if (Describe(board, m).Plain == wanted)
            {
                found = m;
                hits++;
            }
        }
        if (hits == 1)
            return found;
        if (hits > 1)
            throw new FormatException($"Ambiguous move '{text}' (use 前/后).");
        throw new FormatException($"Illegal or unrecognised move '{text}' for {(side == Color.Red ? "red" : "black")}.");
    }

    private static string Normalize(string text, Color side)
    {
        bool red = side == Color.Red;
        var sb = new StringBuilder();
        foreach (char raw in text.Trim())
        {
            char c = raw;
            int fw = FullWidth.IndexOf(c);
            if (fw >= 0) c = BlackNums[fw];
            int n = RedNums.IndexOf(c);
            if (n < 0) n = BlackNums.IndexOf(c);
            if (n >= 0)
            {
                sb.Append(red ? RedNums[n] : BlackNums[n]);
                continue;
            }
            sb.Append(c switch
            {
                '車' or '俥' or '伡' => '车',
                '馬' or '傌' or '㐷' => '马',
                '相' or '象' => red ? '相' : '象',
                '仕' or '士' => red ? '仕' : '士',
                '帅' or '帥' or '将' or '將' => red ? '帅' : '将',
                '砲' or '包' or '炮' => '炮',
                '兵' or '卒' => red ? '兵' : '卒',
                '後' => '后',
                '進' => '进',
                _ => c,
            });
        }
        return sb.ToString().Replace(" ", "");
    }

    private static string Name(PieceType t, bool red) => t switch
    {
        PieceType.Chariot => "车",
        PieceType.Horse => "马",
        PieceType.Elephant => red ? "相" : "象",
        PieceType.Advisor => red ? "仕" : "士",
        PieceType.General => red ? "帅" : "将",
        PieceType.Cannon => "炮",
        PieceType.Soldier => red ? "兵" : "卒",
        _ => throw new FormatException("No piece."),
    };

    private static char Num(int n, bool red) => red ? RedNums[n - 1] : BlackNums[n - 1];

    private static int FileNum(int file, bool red) => red ? 9 - file : file + 1;

    private static (string Canonical, string Plain) Describe(Board board, Move move)
    {
        Piece piece = board.GetPiece(move.FromFile, move.FromRank);
        if (piece == Piece.None)
            throw new FormatException("No piece on the from-square.");
        bool red = piece.GetColor() == Color.Red;
        PieceType type = piece.GetPieceType();
        string name = Name(type, red);

        string action;
        int number;
        if (move.FromRank == move.ToRank)
        {
            action = "平";
            number = FileNum(move.ToFile, red);
        }
        else
        {
            bool forward = red ? move.ToRank > move.FromRank : move.ToRank < move.FromRank;
            action = forward ? "进" : "退";
            number = type is PieceType.Horse or PieceType.Elephant or PieceType.Advisor
                ? FileNum(move.ToFile, red)
                : Math.Abs(move.ToRank - move.FromRank);
        }
        string tail = action + Num(number, red);
        string plain = name + Num(FileNum(move.FromFile, red), red) + tail;

        if (type == PieceType.General)
            return (plain, plain);

        // Same pieces on the same file, ordered from most advanced (toward the opponent).
        var ranks = new List<int>();
        for (int r = 0; r < Board.RankCount; r++)
            if (board.GetPiece(move.FromFile, r) == piece)
                ranks.Add(r);
        if (ranks.Count < 2)
            return (plain, plain);
        ranks.Sort();
        if (red) ranks.Reverse();
        int idx = ranks.IndexOf(move.FromRank);
        string prefix = ranks.Count switch
        {
            2 => idx == 0 ? "前" : "后",
            3 => idx == 0 ? "前" : idx == 1 ? "中" : "后",
            _ => Num(idx + 1, red).ToString(),
        };
        return (prefix + name + tail, plain);
    }
}
