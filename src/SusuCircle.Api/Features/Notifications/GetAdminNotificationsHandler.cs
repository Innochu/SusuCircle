using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Features.Notifications;

// ── Requests ──────────────────────────────────────────────────────────────────

// category is null => "All" tab. Otherwise Payments / Payouts / System.
public record GetAdminNotificationsQuery(
    Guid AdminId,
    AdminNotificationCategory? Category = null,
    int Page = 1,
    int PageSize = 20) : IRequest<AdminNotificationsResponse>;

public record MarkAdminNotificationsReadCommand(Guid AdminId, Guid? NotificationId = null) : IRequest<int>;

public record AdminNotificationsResponse(
    int UnreadCount,
    int Total,
    int Page,
    int PageSize,
    List<AdminNotificationDto> Items);

public record AdminNotificationDto(
    Guid Id,
    string Type,
    string Category,
    string Title,
    string Body,
    string? CircleName,
    bool IsRead,
    DateTime CreatedAt);

// ── Query handler ─────────────────────────────────────────────────────────────

public class GetAdminNotificationsHandler(AppDbContext db)
    : IRequestHandler<GetAdminNotificationsQuery, AdminNotificationsResponse>
{
    public async Task<AdminNotificationsResponse> Handle(GetAdminNotificationsQuery q, CancellationToken ct)
    {
        _ = await db.Admins.FindAsync([q.AdminId], ct)
            ?? throw new NotFoundException(nameof(Admin), q.AdminId);

        var baseQuery = db.AdminNotifications.Where(n => n.AdminId == q.AdminId);

        // Unread count is always across all categories (the badge in the sidebar)
        var unreadCount = await baseQuery.CountAsync(n => !n.IsRead, ct);

        var filtered = baseQuery;
        if (q.Category.HasValue)
            filtered = filtered.Where(n => n.Category == q.Category.Value);

        var total = await filtered.CountAsync(ct);

        var items = await filtered
            .OrderByDescending(n => n.CreatedAt) 
            .Skip((q.Page - 1) * q.PageSize)
            .Take(q.PageSize)
            .Select(n => new AdminNotificationDto(
                n.Id,
                n.Type.ToString(),
                n.Category.ToString(),
                n.Title,
                n.Body,
                n.CircleName,
                n.IsRead,
                n.CreatedAt))
            .ToListAsync(ct);

        return new AdminNotificationsResponse(unreadCount, total, q.Page, q.PageSize, items);
    }
}

// ── Mark-read handler ─────────────────────────────────────────────────────────

// NotificationId null => mark all read ("Mark all read" link). Otherwise mark one.
public class MarkAdminNotificationsReadHandler(AppDbContext db)
    : IRequestHandler<MarkAdminNotificationsReadCommand, int>
{
    public async Task<int> Handle(MarkAdminNotificationsReadCommand cmd, CancellationToken ct)
    {
        var query = db.AdminNotifications
            .Where(n => n.AdminId == cmd.AdminId && !n.IsRead);

        if (cmd.NotificationId.HasValue)
            query = query.Where(n => n.Id == cmd.NotificationId.Value);

        return await query.ExecuteUpdateAsync(
            s => s.SetProperty(n => n.IsRead, true), ct);
    }
}

// ── Endpoints ─────────────────────────────────────────────────────────────────

public static class AdminNotificationEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/{adminId:guid}/notifications",
            async (Guid adminId, IMediator mediator, string? category = null, int page = 1, int pageSize = 20) =>
            {
                AdminNotificationCategory? cat = category is null
                    ? null
                    : Enum.Parse<AdminNotificationCategory>(category, ignoreCase: true);

                var result = await mediator.Send(
                    new GetAdminNotificationsQuery(adminId, cat, page, pageSize));
                return Results.Ok(ApiResponse<AdminNotificationsResponse>.Ok(result));
            })
            .WithName("GetAdminNotifications")
            .WithTags("Notifications")
            .AllowAnonymous();

        // Mark all read
        app.MapPatch("/api/admin/{adminId:guid}/notifications/read",
            async (Guid adminId, IMediator mediator) =>
            {
                var count = await mediator.Send(new MarkAdminNotificationsReadCommand(adminId));
                return Results.Ok(ApiResponse<object>.Ok(new { markedRead = count }));
            })
            .WithName("MarkAllAdminNotificationsRead")
            .WithTags("Notifications")
            .AllowAnonymous();

        // Mark one read
        app.MapPatch("/api/admin/{adminId:guid}/notifications/{notificationId:guid}/read",
            async (Guid adminId, Guid notificationId, IMediator mediator) =>
            {
                var count = await mediator.Send(
                    new MarkAdminNotificationsReadCommand(adminId, notificationId));
                return Results.Ok(ApiResponse<object>.Ok(new { markedRead = count }));
            })
            .WithName("MarkAdminNotificationRead")
            .WithTags("Notifications")
            .AllowAnonymous();
    }
}
