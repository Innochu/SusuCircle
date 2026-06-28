using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Common.Services;

public interface INotificationService
{
    Task SendAsync(Guid memberId, NotificationType type, string title, string body, CancellationToken ct = default);
    Task SendBulkAsync(IEnumerable<Guid> memberIds, NotificationType type, string title, string body, CancellationToken ct = default);
}

public class NotificationService(AppDbContext db, ILogger<NotificationService> logger) : INotificationService
{
    public async Task SendAsync(Guid memberId, NotificationType type, string title, string body, CancellationToken ct = default)
    {
        var notification = new Notification
        {
            Id       = Guid.NewGuid(),
            MemberId = memberId,
            Type     = type,
            Title    = title,
            Body     = body,
        };
        db.Notifications.Add(notification);
        await db.SaveChangesAsync(ct);

        // TODO Sprint 4: send SMS via Termii and/or email via SendGrid here
        logger.LogInformation("Notification sent to member {MemberId}: {Title}", memberId, title);
    }

    public async Task SendBulkAsync(IEnumerable<Guid> memberIds, NotificationType type, string title, string body, CancellationToken ct = default)
    {
        var notifications = memberIds.Select(id => new Notification
        {
            Id       = Guid.NewGuid(),
            MemberId = id,
            Type     = type,
            Title    = title,
            Body     = body,
        });
        db.Notifications.AddRange(notifications);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Bulk notification sent to {Count} members: {Title}", memberIds.Count(), title);
    }
}
