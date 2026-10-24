using System.Text.RegularExpressions;

namespace Xiangqi.Core;

/// <summary>Parses move lists (ICCS coordinates and/or 中文记谱) and replays them.</summary>
public static partial class GameRecord
{
    [GeneratedRegex(@"^[a-iA-I][0-9]-?[a-iA-I][0-9]$")]
    private static partial Regex Iccs();

    [GeneratedRegex(@"^\d+\s*[\.．、:：)]+")]
    private static partial Regex MoveNumber();

    private static readonly HashSet<string> ResultTokens = new() { "1-0", "0-1", "1/2-1/2", "½-½", "*", "红胜", "黑胜", "和棋" };

    public static IReadOnlyList<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return tokens;
        foreach (string piece in text.Split(new[] { ' ', '\t', '\r', '\n', ',', '，', '。', '；', ';', '、' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string t = piece.Trim();
            while (true)
            {
                var m = MoveNumber().Match(t);
                if (!m.Success) break;
                t = t[m.Length..];
            }
            if (t.Length == 0 || ResultTokens.Contains(t) || Regex.IsMatch(t, @"^\d+$"))
                continue;
            if (!Iccs().IsMatch(t) && t.Length > 4 && t.Length % 4 == 0 && !t.Any(char.IsAsciiLetter))
            {
                for (int i = 0; i < t.Length; i += 4)
                    tokens.Add(t.Substring(i, 4));
                continue;
            }
            tokens.Add(t);
        }
        return tokens;
    }

    public static IReadOnlyList<Move> ParseMoves(string startFen, string text)
    {
        var board = Board.FromFen(startFen);
        var moves = new List<Move>();
        var tokens = Tokenize(text);
        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i];
            Move move;
            try
            {
                if (Iccs().IsMatch(token))
                {
                    move = Move.Parse(token.Replace("-", "").ToLowerInvariant());
                    if (!board.GenerateLegalMoves().Contains(move))
                        throw new FormatException("illegal move");
                }
                else
                {
                    move = ChineseNotation.Parse(board, token);
                }
            }
            catch (FormatException ex)
            {
                throw new FormatException($"第 {i + 1} 步 '{token}' 无法解析 / ply {i + 1} '{token}': {ex.Message}");
            }
            board.MakeMove(move);
            moves.Add(move);
        }
        return moves;
    }

    public static (string Fen, IReadOnlyList<string> Fens) Replay(string startFen, IReadOnlyList<Move> moves)
    {
        var board = Board.FromFen(startFen);
        var fens = new List<string> { board.ToFen() };
        foreach (var m in moves)
        {
            board.MakeMove(m);
            fens.Add(board.ToFen());
        }
        return (fens[^1], fens);
    }

    public static string FenAtPly(string startFen, IReadOnlyList<Move> moves, int ply)
    {
        if (ply < 0 || ply > moves.Count)
            throw new ArgumentOutOfRangeException(nameof(ply), $"ply must be 0..{moves.Count}.");
        var board = Board.FromFen(startFen);
        for (int i = 0; i < ply; i++)
            board.MakeMove(moves[i]);
        return board.ToFen();
    }

    /// <summary>Chinese notation for each move, computed on the fly.</summary>
    public static IReadOnlyList<string> ToChinese(string startFen, IReadOnlyList<Move> moves)
    {
        var board = Board.FromFen(startFen);
        var list = new List<string>();
        foreach (var m in moves)
        {
            list.Add(ChineseNotation.Format(board, m));
            board.MakeMove(m);
        }
        return list;
    }
}
