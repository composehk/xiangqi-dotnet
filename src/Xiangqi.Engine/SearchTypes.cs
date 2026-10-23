using Xiangqi.Core;

namespace Xiangqi.Engine;

public enum Difficulty
{
    Easy,
    Medium,
    Hard
}

/// <summary>
/// Depth and optional wall-clock limit in milliseconds. Zero time means no time limit.
/// </summary>
public readonly record struct SearchLimits
{
    public int Depth { get; init; }
    public int TimeMs { get; init; }

    public SearchLimits(int depth, int timeMs = 0)
    {
        Depth = depth;
        TimeMs = timeMs;
    }

    public static SearchLimits For(Difficulty difficulty) => difficulty switch
    {
        Difficulty.Easy => new SearchLimits(2, 0),
        Difficulty.Medium => new SearchLimits(4, 0),
        Difficulty.Hard => new SearchLimits(6, 5_000),
        _ => throw new ArgumentOutOfRangeException(nameof(difficulty))
    };
}

/// <summary>
/// <paramref name="Score"/> is from the side to move's point of view.
/// </summary>
public readonly record struct EngineResult(Move BestMove, int Score, int Depth, long Nodes);

public interface IEngine
{
    Task<EngineResult> SearchAsync(Board board, SearchLimits limits, CancellationToken cancellationToken);
}
