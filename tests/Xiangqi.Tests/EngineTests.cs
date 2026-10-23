using Xiangqi.Core;
using Xiangqi.Engine;

namespace Xiangqi.Tests;

public class EngineTests
{
    [Fact]
    public void DifficultyPresets()
    {
        Assert.Equal(2, SearchLimits.For(Difficulty.Easy).Depth);
        Assert.Equal(0, SearchLimits.For(Difficulty.Easy).TimeMs);
        Assert.Equal(4, SearchLimits.For(Difficulty.Medium).Depth);
        Assert.Equal(0, SearchLimits.For(Difficulty.Medium).TimeMs);
        Assert.Equal(6, SearchLimits.For(Difficulty.Hard).Depth);
        Assert.True(SearchLimits.For(Difficulty.Hard).TimeMs > 0);
    }

    [Fact]
    public async Task EngineReturnsLegalMove()
    {
        var board = Board.FromFen(Board.StartFen);
        string fen = board.ToFen();
        IEngine engine = new AlphaBetaEngine();

        EngineResult result = await engine.SearchAsync(
            board,
            SearchLimits.For(Difficulty.Easy),
            CancellationToken.None);

        Assert.Contains(result.BestMove, board.GenerateLegalMoves());
        Assert.False(result.BestMove.IsNone);
        Assert.True(result.Depth >= 1);
        Assert.True(result.Nodes > 0);
        Assert.Equal(fen, board.ToFen());
    }

    [Fact]
    public void UciEngineImplementsIEngine()
    {
        Assert.True(typeof(IEngine).IsAssignableFrom(typeof(UciEngine)));
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(UciEngine)));
        Assert.True(typeof(IEngine).IsAssignableFrom(typeof(AlphaBetaEngine)));
    }
}
