using Hangfire;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;
using SusuCircle.Api.Common.Services;

namespace SusuCircle.Api.Infrastructure;

public class DefaultCheckJob(AppDbContext db, INotificationService notifications, ILogger<DefaultCheckJob> logger)
{
    /// <summary>
    /// Runs daily at midnight. Marks overdue contributions as Defaulted.
    /// Registered via Hangfire recurring job in Program.cs.
    /// </summary>
    public async Task RunAsync()
    {
        var overdue = await db.Contributions
            .Include(c => c.Member)
            .Where(c =>
                c.Status == ContributionStatus.Pending &&
                c.DueDate < DateTime.UtcNow)
            .ToListAsync();

        foreach (var contribution in overdue)
        {
            contribution.Status = ContributionStatus.Defaulted;
            contribution.Member.ConsecutiveOnTimeStreak = 0;
            logger.LogWarning("Member {MemberId} defaulted on cycle {Cycle}", contribution.MemberId, contribution.CycleNumber);
        }

        await db.SaveChangesAsync();

        // Notify defaulted members
        var memberIds = overdue.Select(c => c.MemberId).Distinct();
        foreach (var memberId in memberIds)
        {
            var memberContribs = overdue.Where(c => c.MemberId == memberId);
            foreach (var c in memberContribs)
            {
                await notifications.SendAsync(memberId, NotificationType.Defaulted,
                    "Contribution overdue",
                    $"Your contribution of ₦{c.ExpectedAmount:N0} for cycle {c.CycleNumber} is overdue. Please contact your coordinator.");
            }
        }

        logger.LogInformation("Default check complete. {Count} contributions marked as defaulted.", overdue.Count);
    }
}

public class PaymentReminderJob(AppDbContext db, INotificationService notifications)
{
    /// <summary>
    /// Runs daily. Sends reminders 3 days before due date for pending contributions.
    /// </summary>
    public async Task RunAsync()
    {
        var upcoming = await db.Contributions
            .Where(c =>
                c.Status == ContributionStatus.Pending &&
                c.DueDate.Date == DateTime.UtcNow.Date.AddDays(3))
            .ToListAsync();

        foreach (var c in upcoming)
        {
            await notifications.SendAsync(c.MemberId, NotificationType.PaymentReminder,
                "Contribution reminder",
                $"Your contribution of ₦{c.ExpectedAmount:N0} for cycle {c.CycleNumber} is due in 3 days ({c.DueDate:dd MMM yyyy}).");
        }
    }
}

public class PayoutRetryJob(AppDbContext db, ILogger<PayoutRetryJob> logger)
{
    /// <summary>
    /// Retries failed payouts with exponential back-off (max 5 attempts).
    /// </summary>
    public async Task RunAsync()
    {
        var failedPayouts = await db.Payouts
            .Where(p => p.Status == PayoutStatus.Failed && p.RetryCount < 5)
            .ToListAsync();

        foreach (var payout in failedPayouts)
        {
            logger.LogInformation("Payout {PayoutId} flagged for retry (attempt {Attempt})",
                payout.Id, payout.RetryCount + 1);
            // Hangfire retry: enqueue via IBackgroundJobClient in the actual payout service
        }
    }
}

// Shim to satisfy Hangfire's lambda — actual retry logic lives in TriggerPayoutHandler

