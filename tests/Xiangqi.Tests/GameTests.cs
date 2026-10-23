using Xiangqi.Core;

namespace Xiangqi.Tests;

public class GameTests
{
    [Fact]
    public void CheckmateIsALossForTheSideToMove()
    {
        var alreadyMate = new Game("3rk4/9/9/9/9/9/9/9/4r4/3K5 w - - 0 1");
        Assert.True(alreadyMate.Board.IsInCheck(Color.Red));
        Assert.Empty(alreadyMate.Board.GenerateLegalMoves());
        Assert.Equal(GameStatus.BlackWins, alreadyMate.Status);

        var game = new Game("3k5/4R4/9/9/4R4/9/9/9/9/4K4 w - - 0 1");
        Assert.Equal(GameStatus.Ongoing, game.Status);
        game.MakeMove("e5d5");
        Assert.True(game.Board.IsInCheck(Color.Black));
        Assert.Empty(game.Board.GenerateLegalMoves());
        Assert.Equal(GameStatus.RedWins, game.Status);
        Assert.Equal("e5d5", game.History[0].ToString());
    }

    [Fact]
    public void StalemateIsALossForTheSideToMove()
    {
        var game = new Game("4k4/9/9/9/9/9/9/9/4r4/3K5 w - - 0 1");
        Assert.False(game.Board.IsInCheck(Color.Red));
        Assert.Empty(game.Board.GenerateLegalMoves());
        Assert.Equal(GameStatus.BlackWins, game.Status);

        var blackStalemate = new Game("3k5/4R4/9/9/9/9/9/9/9/4K4 b - - 0 1");
        Assert.False(blackStalemate.Board.IsInCheck(Color.Black));
        Assert.Empty(blackStalemate.Board.GenerateLegalMoves());
        Assert.Equal(GameStatus.RedWins, blackStalemate.Status);
    }

    [Fact]
    public void CheckmateTakesPriorityOverTheNoCaptureLimit()
    {
        var game = new Game("3rk4/9/9/9/9/9/9/9/4r4/3K5 w - - 120 40");
        Assert.Equal(GameStatus.BlackWins, game.Status);
    }

    [Fact]
    public void OneHundredTwentyPliesWithoutCaptureIsADraw()
    {
        var expired = new Game("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 120 60");
        Assert.Equal(GameStatus.Draw, expired.Status);

        var quiet = new Game("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 119 60");
        Assert.Equal(GameStatus.Ongoing, quiet.Status);
        quiet.MakeMove("a3a4");
        Assert.Equal(GameStatus.Draw, quiet.Status);

        var capture = new Game("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 119 60");
        capture.MakeMove("b2b9");
        Assert.Equal(GameStatus.Ongoing, capture.Status);
        Assert.Equal(0, capture.Board.HalfmoveClock);
    }

    [Fact]
    public void ThreefoldRepetitionIsADraw()
    {
        var game = new Game();
        string[] cycle = { "b0a2", "b9a7", "a2b0", "a7b9" };
        for (int i = 0; i < 4; i++)
            game.MakeMove(cycle[i]);

        Assert.Equal(GameStatus.Ongoing, game.Status);
        Assert.Equal(
            "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 4 3",
            game.Fen);

        for (int i = 0; i < 4; i++)
            game.MakeMove(cycle[i]);

        Assert.Equal(GameStatus.Draw, game.Status);
        Assert.Equal(8, game.History.Count);
    }
}
