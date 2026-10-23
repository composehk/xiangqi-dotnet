using Microsoft.AspNetCore.SignalR;

namespace Xiangqi.Server;

/// <summary>
/// One SignalR group per room. Clients invoke <c>JoinRoom</c> and listen for <c>state</c>.
/// </summary>
public sealed class GameHub : Hub
{
    private readonly ArenaService _arena;

    public GameHub(ArenaService arena)
    {
        _arena = arena;
    }

    public Task JoinRoom(string roomId)
    {
        if (!Guid.TryParse(roomId, out Guid id) || !_arena.Exists(id))
            throw new HubException("Room not found.");

        return Groups.AddToGroupAsync(Context.ConnectionId, ArenaService.GroupName(id));
    }

    public Task LeaveRoom(string roomId)
    {
        if (!Guid.TryParse(roomId, out Guid id))
            return Task.CompletedTask;

        return Groups.RemoveFromGroupAsync(Context.ConnectionId, ArenaService.GroupName(id));
    }
}
