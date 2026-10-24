using Xiangqi.Core;

namespace Xiangqi.Tests;

public class ChineseNotationTests
{
    [Theory]
    [InlineData("炮二平五", "h2e2")]
    [InlineData("马二进三", "h0g2")]
    [InlineData("马八进七", "b0c2")]
    [InlineData("车九进一", "a0a1")]
    [InlineData("兵七进一", "c3c4")]
    [InlineData("相三进五", "g0e2")]
    [InlineData("仕四进五", "f0e1")]
    [InlineData("帅五进一", "e0e1")]
    [InlineData("俥一進一", "i0i1")]
    public void RedFromStart(string text, string iccs)
    {
        var board = Board.FromFen(Board.StartFen);
        Assert.Equal(iccs, ChineseNotation.Parse(board, text).ToString());
    }

    [Fact]
    public void BlackUsesArabicDigits()
    {
        var board = Board.FromFen(Board.StartFen);
        board.MakeMove(Move.Parse("h2e2"));
        Assert.Equal("h9g7", ChineseNotation.Parse(board, "马8进7").ToString());
        Assert.Equal("b7e7", ChineseNotation.Parse(board, "炮2平5").ToString());
        Assert.Equal("i9i8", ChineseNotation.Parse(board, "车9进1").ToString());
        Assert.Equal("c6c5", ChineseNotation.Parse(board, "卒3进1").ToString());
        Assert.Equal("c6c5", ChineseNotation.Parse(board, "卒３进１").ToString());
        Assert.Equal("马8进7", ChineseNotation.Format(board, Move.Parse("h9g7")));
    }

    [Fact]
    public void FrontAndBackChariots()
    {
        // Red chariots on file e (五) at ranks 3 and 1.
        var board = Board.FromFen("5k3/9/9/9/9/9/4R4/9/4R4/3K5 w - - 0 1");
        Assert.Equal("e3e5", ChineseNotation.Parse(board, "前车进二").ToString());
        Assert.Equal("e1d1", ChineseNotation.Parse(board, "后车平六").ToString());
        Assert.Equal("e1d1", ChineseNotation.Parse(board, "後車平六").ToString());
        Assert.Equal("前车进二", ChineseNotation.Format(board, Move.Parse("e3e5")));
        Assert.Throws<FormatException>(() => ChineseNotation.Parse(board, "车五平六"));
    }

    [Fact]
    public void FrontAndBackForBlack()
    {
        // Black cannons on file b at ranks 7 and 5; black's "front" is the lower rank.
        var board = Board.FromFen("4k4/9/1c7/9/1c7/9/9/9/9/4K4 b - - 0 1");
        Assert.Equal("前炮平5", ChineseNotation.Format(board, Move.Parse("b5e5")));
        Assert.Equal("后炮平5", ChineseNotation.Format(board, Move.Parse("b7e7")));
        Assert.Equal("b5e5", ChineseNotation.Parse(board, "前炮平5").ToString());
    }

    [Theory]
    [InlineData(Board.StartFen)]
    [InlineData("r1bakab1r/9/1cn3nc1/p1p1p1p1p/9/9/P1P1P1P1P/1CN3NC1/9/R1BAKAB1R w - - 0 1")]
    [InlineData("5k3/9/9/9/9/9/4R4/9/4R4/3K5 w - - 0 1")]
    public void RoundTripAllLegalMoves(string fen)
    {
        var board = Board.FromFen(fen);
        foreach (var m in board.GenerateLegalMoves())
            Assert.Equal(m, ChineseNotation.Parse(board, ChineseNotation.Format(board, m)));
    }

    [Fact]
    public void IllegalMoveThrows()
    {
        var board = Board.FromFen(Board.StartFen);
        Assert.Throws<FormatException>(() => ChineseNotation.Parse(board, "车一进九"));
        Assert.Throws<FormatException>(() => ChineseNotation.Parse(board, "马二进四"));
    }

    [Fact]
    public void GameRecordParsesChineseAndIccs()
    {
        var a = GameRecord.ParseMoves(Board.StartFen, "1. 炮二平五 马8进7\n2. 马二进三 车9平8 *");
        Assert.Equal(new[] { "h2e2", "h9g7", "h0g2", "i9h9" }, a.Select(m => m.ToString()));
        var b = GameRecord.ParseMoves(Board.StartFen, "h2e2 H9-G7, h0g2");
        Assert.Equal(new[] { "h2e2", "h9g7", "h0g2" }, b.Select(m => m.ToString()));
        var c = GameRecord.ParseMoves(Board.StartFen, "1.炮二平五马8进7");
        Assert.Equal(2, c.Count);
        var ex = Assert.Throws<FormatException>(() => GameRecord.ParseMoves(Board.StartFen, "炮二平五 车一进九"));
        Assert.Contains("2", ex.Message);
    }

    [Fact]
    public void ReplayAndBreakpoint()
    {
        var moves = GameRecord.ParseMoves(Board.StartFen, "炮二平五 马8进7 马二进三");
        var (fen, fens) = GameRecord.Replay(Board.StartFen, moves);
        Assert.Equal(4, fens.Count);
        Assert.Equal(Board.FromFen(Board.StartFen).ToFen(), fens[0]);
        Assert.Equal(fen, fens[3]);
        string atTwo = GameRecord.FenAtPly(Board.StartFen, moves, 2);
        Assert.Equal(fens[2], atTwo);
        Assert.Equal(Color.Red, Board.FromFen(atTwo).SideToMove);
        Assert.Throws<ArgumentOutOfRangeException>(() => GameRecord.FenAtPly(Board.StartFen, moves, 4));
        Assert.Equal(new[] { "炮二平五", "马8进7", "马二进三" }, GameRecord.ToChinese(Board.StartFen, moves));
    }
}
