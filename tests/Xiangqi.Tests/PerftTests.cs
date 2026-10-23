using System.Text;
using Xiangqi.Core;

namespace Xiangqi.Tests;

public class PerftTests
{
    [Fact]
    public void StartPositionPerft()
    {
        var board = Board.FromFen(Board.StartFen);
        AssertPerft(board, 1, 44);
        AssertPerft(board, 2, 1920);
        AssertPerft(board, 3, 79666);
    }

    [Fact]
    public void StartPositionRoundTripsAndHasTheKnownMoves()
    {
        var board = Board.FromFen(Board.StartFen);
        Assert.Equal(Board.StartFen, board.ToFen());
        Assert.Equal(Color.Red, board.SideToMove);

        List<Move> legal = board.GenerateLegalMoves();
        List<Move> pseudo = board.GeneratePseudoLegalMoves();
        Assert.Equal(44, legal.Count);
        Assert.Equal(44, pseudo.Count);
        Assert.Contains(Move.Parse("h2e2"), legal);
        Assert.Contains(Move.Parse("b2b9"), legal);
        Assert.Contains(Move.Parse("h2h9"), legal);
        Assert.Contains(Move.Parse("a3a4"), legal);
        Assert.Contains(Move.Parse("b0a2"), legal);
        Assert.Contains(Move.Parse("b0c2"), legal);
        Assert.DoesNotContain(Move.Parse("b0d1"), legal);
        Assert.Contains(Move.Parse("e0e1"), legal);
        Assert.Contains(Move.Parse("d0e1"), legal);
    }

    [Fact]
    public void MakeMoveUpdatesFenAndUnmakeRestoresIt()
    {
        var board = Board.FromFen(Board.StartFen);
        board.MakeMove(Move.Parse("h2e2"));
        Assert.Equal(
            "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C2C4/9/RNBAKABNR b - - 1 1",
            board.ToFen());

        board.UnmakeMove();
        Assert.Equal(Board.StartFen, board.ToFen());

        board.MakeMove(Move.Parse("b2b9"));
        Assert.Equal(
            "rCbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/7C1/9/RNBAKABNR b - - 0 1",
            board.ToFen());
        board.UnmakeMove();
        Assert.Equal(Board.StartFen, board.ToFen());
    }

    [Fact]
    public void MakeUnmakePreservesFenAndHash()
    {
        var board = Board.FromFen(Board.StartFen);
        Walk(board, 2);
    }

    private static void Walk(Board board, int depth)
    {
        string fen = board.ToFen();
        ulong hash = board.Hash;
        foreach (Move move in board.GenerateLegalMoves())
        {
            board.MakeMove(move);
            Board replay = Board.FromFen(board.ToFen());
            Assert.Equal(replay.ToFen(), board.ToFen());
            Assert.Equal(replay.Hash, board.Hash);
            if (depth > 1)
                Walk(board, depth - 1);
            board.UnmakeMove();
            Assert.Equal(fen, board.ToFen());
            Assert.Equal(hash, board.Hash);
        }
    }

    private static void AssertPerft(Board board, int depth, long expected)
    {
        long got = board.Perft(depth);
        if (got == expected)
            return;

        var message = new StringBuilder();
        message.AppendLine($"Perft({depth}) = {got}, expected {expected}.");
        foreach ((Move move, long nodes) in board.PerftDivide(depth))
            message.AppendLine($"{move} {nodes}");
        Assert.Fail(message.ToString());
    }
}
