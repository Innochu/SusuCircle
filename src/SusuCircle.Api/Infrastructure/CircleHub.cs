using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace SusuCircle.Api.Infrastructure;

/// <summary>
/// SignalR hub for real-time contribution board updates.
/// Frontend connects and joins a circle group by circleId.
/// Server pushes "ContributionUpdated" events on every reconciled payment.
/// </summary>
[Authorize]
public class CircleHub : Hub
{
    public async Task JoinCircle(string circleId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, circleId);
    }

    public async Task LeaveCircle(string circleId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, circleId);
    }
}
