using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Features.Notifications.GetNotifications;

public record GetNotificationsQuery(Guid MemberId, int Page = 1, int PageSize = 20) : IRequest<PagedResponse<NotificationDto>>;

public record MarkNotificationsReadCommand(Guid MemberId) : IRequest<bool>;

public record NotificationDto(Guid Id, NotificationType Type, string Title, string Body, bool IsRead, DateTime CreatedAt);

public class GetNotificationsHandler(AppDbContext db) : IRequestHandler<GetNotificationsQuery, PagedResponse<NotificationDto>>
{
    public async Task<PagedResponse<NotificationDto>> Handle(GetNotificationsQuery q, CancellationToken ct)
    {
        _ = await db.Members.FindAsync([q.MemberId], ct)
            ?? throw new NotFoundException(nameof(Member), q.MemberId);

        var query = db.Notifications.Where(n => n.MemberId == q.MemberId);
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(n => n.CreatedAt)
            .Skip((q.Page - 1) * q.PageSize)
            .Take(q.PageSize)
            .Select(n => new NotificationDto(n.Id, n.Type, n.Title, n.Body, n.IsRead, n.CreatedAt))
            .ToListAsync(ct);

        return new PagedResponse<NotificationDto>(items, total, q.Page, q.PageSize);
    }
}

public class MarkNotificationsReadHandler(AppDbContext db) : IRequestHandler<MarkNotificationsReadCommand, bool>
{
    public async Task<bool> Handle(MarkNotificationsReadCommand cmd, CancellationToken ct)
    {
        await db.Notifications
            .Where(n => n.MemberId == cmd.MemberId && !n.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true), ct);
        return true;
    }
}

public static class NotificationEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/members/{memberId:guid}/notifications",
            async (Guid memberId, IMediator mediator, int page = 1, int pageSize = 20) =>
            {
                var result = await mediator.Send(new GetNotificationsQuery(memberId, page, pageSize));
                return Results.Ok(ApiResponse<PagedResponse<NotificationDto>>.Ok(result));
            })
            .WithName("GetNotifications")
            .WithTags("Notifications")
            .AllowAnonymous();

        app.MapPatch("/api/members/{memberId:guid}/notifications/read",
            async (Guid memberId, IMediator mediator) =>
            {
                await mediator.Send(new MarkNotificationsReadCommand(memberId));
                return Results.Ok(ApiResponse<string>.Ok("Notifications marked as read."));
            })
            .WithName("MarkNotificationsRead")
            .WithTags("Notifications")
            .AllowAnonymous();
    }
}
