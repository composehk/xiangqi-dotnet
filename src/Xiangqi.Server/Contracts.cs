using Microsoft.AspNetCore.Http;

namespace Xiangqi.Server;

public sealed record HealthDto(string Status, string Version, string BuiltAt);

public sealed record ErrorDto(string Error);

public sealed record CreateRoomRequest(string? Name, string? RedKind, string? BlackKind, string? Difficulty);

public sealed record VsAiRequest(string? HumanColor, string? Difficulty);

public sealed record MoveRequest(string? Move, string? Token);

public sealed record JoinRequest(string? Color, string? Kind);

public sealed record SeatDto(string Kind, bool Occupied, string? Difficulty);

public sealed record SeatsDto(SeatDto Red, SeatDto Black);

/// <summary>
/// Public game view. <c>board[0]</c> is the top rank (Black's back rank);
/// <c>board[9]</c> is Red's back rank. Each rank has nine files, a through i.
/// Cells are Xiangqi FEN characters, or "" when empty.
/// </summary>
public sealed record GameStateDto(
    Guid Id,
    string Name,
    string Fen,
    string[][] Board,
    string SideToMove,
    string Status,
    bool InCheck,
    string? LastMove,
    string[] History,
    SeatsDto Seats);

public sealed record RoomSummaryDto(
    Guid Id,
    string Name,
    string Status,
    string SideToMove,
    int MoveCount,
    SeatsDto Seats);

public sealed record SeatTokensDto(string? Red, string? Black);

public sealed record CreateRoomResponseDto(Guid Id, string Name, SeatTokensDto Tokens, GameStateDto State);

public sealed record VsAiResponseDto(Guid RoomId, string Token, GameStateDto State);

public sealed record JoinResponseDto(string Token, string Color, GameStateDto State);

internal sealed class ApiException : Exception
{
    public ApiException(int status, string message)
        : base(message)
    {
        Status = status;
    }

    public int Status { get; }
}

internal static class ApiResults
{
    public static IResult Of(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (ApiException ex)
        {
            return Fail(ex);
        }
    }

    public static async Task<IResult> OfAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ApiException ex)
        {
            return Fail(ex);
        }
    }

    private static IResult Fail(ApiException ex) =>
        Results.Json(new ErrorDto(ex.Message), statusCode: ex.Status);
}
