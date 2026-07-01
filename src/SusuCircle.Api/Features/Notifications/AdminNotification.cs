namespace SusuCircle.Api.Common.Models;

// ── Entity ────────────────────────────────────────────────────────────────────
// Admin-facing notifications shown on the Notifications screen (image 7).
// Separate from the member-facing Notification entity because the audience,
// filtering tabs, and event set differ.
//
// Add to AppDbContext:
//     public DbSet<AdminNotification> AdminNotifications => Set<AdminNotification>();
//
// Migration:
//     Add-Migration AdminNotifications   (PMC)  /  dotnet ef migrations add AdminNotifications (CLI)

public class AdminNotification
{
    public Guid Id { get; set; }
    public Guid AdminId { get; set; }

    public AdminNotificationType Type { get; set; }
    public AdminNotificationCategory Category { get; set; }

    public string Title { get; set; } = default!;
    public string Body { get; set; } = default!;

    // Context for grouping / deep-linking (e.g. the circle name shown under each row)
    public Guid? CircleId { get; set; }
    public string? CircleName { get; set; }

    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Drives the icon shown on each row
public enum AdminNotificationType
{
    PaymentReceived,     // green check
    PartialPayment,      // green check (partial wording)
    PaymentOverdue,      // red bell
    NewMemberJoined,     // person icon
    PayoutTriggered,     // payout icon
    PayoutCompleted,
    PayoutFailed,
    System
}

// Drives the filter tabs: All / Payments / Payouts / System
public enum AdminNotificationCategory
{
    Payments,
    Payouts,
    System
}
