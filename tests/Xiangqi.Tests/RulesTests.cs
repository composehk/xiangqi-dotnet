using Xiangqi.Core;

namespace Xiangqi.Tests;

public class RulesTests
{
    [Fact]
    public void FlyingGeneralIsCheckAndPinsTheInterposingPiece()
    {
        var facing = Board.FromFenUnchecked("4k4/9/9/9/9/9/9/9/9/4K4 w - - 0 1");
        Assert.True(facing.IsInCheck(Color.Red));
        Assert.True(facing.IsInCheck(Color.Black));

        List<string> kingMoves = MovesFrom(facing, "e0");
        Assert.Equal(new[] { "e0d0", "e0f0" }, kingMoves);
        Assert.DoesNotContain("e0e1", kingMoves);

        var blocked = Board.FromFenUnchecked("4k4/9/9/9/9/4R4/9/9/9/4K4 w - - 0 1");
        Assert.False(blocked.IsInCheck());
        List<string> chariot = MovesFrom(blocked, "e4");
        Assert.Contains("e4e5", chariot);
        Assert.Contains("e4e9", chariot);
        Assert.DoesNotContain("e4d4", chariot);
        Assert.DoesNotContain("e4f4", chariot);
        Assert.DoesNotContain("e4a4", chariot);

        blocked.MakeMove(Move.Parse("e4e5"));
        // The chariot is still the only screen, so it now checks the black general directly.
        Assert.True(blocked.IsInCheck(Color.Black));
        Assert.False(blocked.IsInCheck(Color.Red));
        blocked.UnmakeMove();
        blocked.MakeMove(Move.Parse("e4d4"));
        Assert.True(blocked.IsInCheck(Color.Red));
    }

    [Fact]
    public void CannonCapturesOnlyWithExactlyOneScreen()
    {
        var board = Board.FromFen("3k5/9/9/r8/9/9/p8/9/9/C3K4 w - - 0 1");
        List<string> moves = MovesFrom(board, "a0");

        Assert.Contains("a0a1", moves);
        Assert.Contains("a0a2", moves);
        Assert.Contains("a0a6", moves);
        Assert.DoesNotContain("a0a3", moves);
        Assert.DoesNotContain("a0a4", moves);
        Assert.DoesNotContain("a0a5", moves);
        Assert.DoesNotContain("a0a9", moves);

        var noScreen = Board.FromFen("3k5/9/9/r8/9/9/9/9/9/C3K4 w - - 0 1");
        List<string> open = MovesFrom(noScreen, "a0");
        Assert.Contains("a0a5", open);
        Assert.DoesNotContain("a0a6", open);
    }

    [Fact]
    public void HorseCannotJumpABlockedLeg()
    {
        var blocked = Board.FromFen("3k5/9/9/9/9/9/9/9/1n7/1N2K4 w - - 0 1");
        List<string> moves = MovesFrom(blocked, "b0");
        Assert.Contains("b0d1", moves);
        Assert.DoesNotContain("b0a2", moves);
        Assert.DoesNotContain("b0c2", moves);

        var open = Board.FromFen(Board.StartFen);
        List<string> start = MovesFrom(open, "b0");
        Assert.Contains("b0a2", start);
        Assert.Contains("b0c2", start);
        Assert.DoesNotContain("b0d1", start);
    }

    [Fact]
    public void ElephantIsBlockedByItsEyeAndCannotCrossTheRiver()
    {
        var board = Board.FromFen("3k5/9/9/9/9/2B6/3n5/9/9/4K4 w - - 0 1");
        List<string> moves = MovesFrom(board, "c4");
        Assert.Contains("c4a2", moves);
        Assert.DoesNotContain("c4e2", moves);
        Assert.DoesNotContain("c4a6", moves);
        Assert.DoesNotContain("c4e6", moves);

        var black = Board.FromFen("3k5/9/9/9/2b6/9/9/9/9/4K4 b - - 0 1");
        List<string> blackMoves = MovesFrom(black, "c5");
        Assert.Contains("c5a7", blackMoves);
        Assert.Contains("c5e7", blackMoves);
        Assert.DoesNotContain("c5a3", blackMoves);
        Assert.DoesNotContain("c5e3", blackMoves);
    }

    [Fact]
    public void SoldierMovesSidewaysOnlyAfterCrossingAndNeverBackward()
    {
        Assert.Equal(new[] { "e4e5" }, MovesFrom(Board.FromFen("3k5/9/9/9/9/4P4/9/9/9/4K4 w - - 0 1"), "e4"));
        Assert.Equal(new[] { "e5d5", "e5e6", "e5f5" }, MovesFrom(Board.FromFen("3k5/9/9/9/4P4/9/9/9/9/4K4 w - - 0 1"), "e5"));
        Assert.Equal(new[] { "e5e4" }, MovesFrom(Board.FromFen("3k5/9/9/9/4p4/9/9/9/9/4K4 b - - 0 1"), "e5"));
        Assert.Equal(new[] { "e4d4", "e4e3", "e4f4" }, MovesFrom(Board.FromFen("3k5/9/9/9/9/4p4/9/9/9/4K4 b - - 0 1"), "e4"));
    }

    [Fact]
    public void CheckMustBeEvaded()
    {
        var board = Board.FromFen("3kr4/9/9/9/9/9/9/9/9/1N1AK4 w - - 0 1");
        Assert.True(board.IsInCheck(Color.Red));

        List<string> legal = board.GenerateLegalMoves().Select(move => move.ToString()).OrderBy(text => text).ToList();
        Assert.Equal(new[] { "d0e1", "e0f0" }, legal);

        List<string> pseudo = board.GeneratePseudoLegalMoves().Select(move => move.ToString()).ToList();
        Assert.Contains("b0a2", pseudo);
        Assert.Contains("b0c2", pseudo);
        Assert.Contains("e0e1", pseudo);
        Assert.DoesNotContain("b0a2", legal);
        Assert.DoesNotContain("e0e1", legal);

        foreach (Move move in board.GenerateLegalMoves())
        {
            board.MakeMove(move);
            Assert.False(board.IsInCheck(Color.Red));
            board.UnmakeMove();
        }
    }

    private static List<string> MovesFrom(Board board, string square)
    {
        Move origin = Move.Parse(square + "a0");
        return board.GenerateLegalMoves()
            .Where(move => move.From == origin.From)
            .Select(move => move.ToString())
            .OrderBy(text => text)
            .ToList();
    }
}
