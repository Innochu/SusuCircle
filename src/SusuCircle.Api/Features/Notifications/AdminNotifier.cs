using Microsoft.AspNetCore.SignalR;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;
using SusuCircle.Api.Infrastructure;

namespace SusuCircle.Api.Common.Services;

// ── Service ───────────────────────────────────────────────────────────────────
// The reconciliation/payout code has no notifications on the ADMIN side yet, which
// is why the screen is empty. This service writes an AdminNotification row AND
// pushes it live over SignalR so the bell/badge updates without refresh.
//
// Register in ServiceExtensions.AddAppServices():
//     services.AddScoped<IAdminNotifier, AdminNotifier>();

public interface IAdminNotifier
{
    Task NotifyAsync(
        Guid adminId,
        AdminNotificationType type,
        string title,
        string body,
        Guid? circleId = null,
        string? circleName = null,
        CancellationToken ct = default);
}

public class AdminNotifier(AppDbContext db, IHubContext<CircleHub> hub) : IAdminNotifier
{
    public async Task NotifyAsync(
        Guid adminId,
        AdminNotificationType type,
        string title,
        string body,
        Guid? circleId = null,
        string? circleName = null,
        CancellationToken ct = default)
    {
        var notification = new AdminNotification
        {
            Id = Guid.NewGuid(),
            AdminId = adminId,
            Type = type,
            Category = MapCategory(type),
            Title = title,
            Body = body,
            CircleId = circleId,
            CircleName = circleName,
            IsRead = false,
            CreatedAt = DateTime.UtcNow,
        };

        db.AdminNotifications.Add(notification);
        await db.SaveChangesAsync(ct);

        // Live push to the admin's personal SignalR group so the badge updates instantly.
        await hub.Clients.Group($"admin-{adminId}")
            .SendAsync("AdminNotification", new
            {
                id = notification.Id,
                type = notification.Type.ToString(),
                category = notification.Category.ToString(),
                title = notification.Title,
                body = notification.Body,
                circleName = notification.CircleName,
                createdAt = notification.CreatedAt,
            }, ct);
    }

    private static AdminNotificationCategory MapCategory(AdminNotificationType type) => type switch
    {
        AdminNotificationType.PaymentReceived => AdminNotificationCategory.Payments,
        AdminNotificationType.PartialPayment => AdminNotificationCategory.Payments,
        AdminNotificationType.PaymentOverdue => AdminNotificationCategory.Payments,
        AdminNotificationType.PayoutTriggered => AdminNotificationCategory.Payouts,
        AdminNotificationType.PayoutCompleted => AdminNotificationCategory.Payouts,
        AdminNotificationType.PayoutFailed => AdminNotificationCategory.Payouts,
        AdminNotificationType.NewMemberJoined => AdminNotificationCategory.System,
        _ => AdminNotificationCategory.System,
    };
}
