using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Features.Members.GetMemberNotifications;

// ══════════════════════════════════════════════════════════════════════════════
// ROUTE CHANGED: /notifications -> /notification-center. Your existing
// NotificationEndpoints (Features/Notifications/) already owns
// "/api/members/{memberId}/notifications" (GET + both PATCH read routes) —
// this endpoint returns a richer shape (UnreadCount included) for the member
// portal's Notifications tab badge, so it needed its own path rather than
// colliding on all three routes at once.
// ══════════════════════════════════════════════════════════════════════════════

public record GetMemberNotificationsQuery(Guid MemberId, int Page = 1, int PageSize = 20)
    : IRequest<MemberNotificationsResponse>;

public record MarkMemberNotificationsReadCommand(Guid MemberId, Guid? NotificationId = null) : IRequest<int>;

public record MemberNotificationsResponse(
    int UnreadCount,
    int Total,
    int Page,
    int PageSize,
    List<MemberNotificationDto> Items);

public record MemberNotificationDto(
    Guid Id, string Type, string Title, string Body, bool IsRead, DateTime CreatedAt);

// ── Query handler ─────────────────────────────────────────────────────────────

public class GetMemberNotificationsHandler(AppDbContext db)
    : IRequestHandler<GetMemberNotificationsQuery, MemberNotificationsResponse>
{
    public async Task<MemberNotificationsResponse> Handle(GetMemberNotificationsQuery q, CancellationToken ct)
    {
        _ = await db.Members.FindAsync([q.MemberId], ct)
            ?? throw new NotFoundException(nameof(Member), q.MemberId);

        var baseQuery = db.Notifications.Where(n => n.MemberId == q.MemberId);

        var unreadCount = await baseQuery.CountAsync(n => !n.IsRead, ct);
        var total = await baseQuery.CountAsync(ct);

        var items = await baseQuery
            .OrderByDescending(n => n.CreatedAt)
            .Skip((q.Page - 1) * q.PageSize)
            .Take(q.PageSize)
            .Select(n => new MemberNotificationDto(
                n.Id, n.Type.ToString(), n.Title, n.Body, n.IsRead, n.CreatedAt))
            .ToListAsync(ct);

        return new MemberNotificationsResponse(unreadCount, total, q.Page, q.PageSize, items);
    }
}

// ── Mark-read handler ─────────────────────────────────────────────────────────

public class MarkMemberNotificationsReadHandler(AppDbContext db)
    : IRequestHandler<MarkMemberNotificationsReadCommand, int>
{
    public async Task<int> Handle(MarkMemberNotificationsReadCommand cmd, CancellationToken ct)
    {
        var query = db.Notifications.Where(n => n.MemberId == cmd.MemberId && !n.IsRead);

        if (cmd.NotificationId.HasValue)
            query = query.Where(n => n.Id == cmd.NotificationId.Value);

        return await query.ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true), ct);
    }
}

// ── Endpoints ─────────────────────────────────────────────────────────────────

public static class MemberNotificationEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/members/{memberId:guid}/notification-center",
            async (Guid memberId, IMediator mediator, int page = 1, int pageSize = 20) =>
            {
                var result = await mediator.Send(new GetMemberNotificationsQuery(memberId, page, pageSize));
                return Results.Ok(ApiResponse<MemberNotificationsResponse>.Ok(result));
            })
            .WithName("GetMemberNotificationCenter")
            .WithTags("Members")
            .AllowAnonymous();

        app.MapPatch("/api/members/{memberId:guid}/notification-center/read",
            async (Guid memberId, IMediator mediator) =>
            {
                var count = await mediator.Send(new MarkMemberNotificationsReadCommand(memberId));
                return Results.Ok(ApiResponse<object>.Ok(new { markedRead = count }));
            })
            .WithName("MarkAllMemberNotificationCenterRead")
            .WithTags("Members")
            .AllowAnonymous();

        app.MapPatch("/api/members/{memberId:guid}/notification-center/{notificationId:guid}/read",
            async (Guid memberId, Guid notificationId, IMediator mediator) =>
            {
                var count = await mediator.Send(new MarkMemberNotificationsReadCommand(memberId, notificationId));
                return Results.Ok(ApiResponse<object>.Ok(new { markedRead = count }));
            })
            .WithName("MarkMemberNotificationCenterOneRead")
            .WithTags("Members")
            .AllowAnonymous();
    }
}