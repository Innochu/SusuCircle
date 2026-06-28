using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Common.Services;

public interface ICreditScoreService
{
    Task RecalculateAsync(Guid memberId, CancellationToken ct = default);
}

/// <summary>
/// ADASHI plan only. Score formula from FRD Section 6.
/// score = 50 + (onTimeRate * 30) + (completionRate * 15) + (defaultRecovery * 5) + streakBonus(max 20)
/// </summary>
public class CreditScoreService(AppDbContext db) : ICreditScoreService
{
    public async Task RecalculateAsync(Guid memberId, CancellationToken ct = default)
    {
        var member = await db.Members
            .Include(m => m.Circle)
            .FirstOrDefaultAsync(m => m.Id == memberId, ct);

        if (member is null || member.Circle.Plan != PlanType.ADASHI) return;

        var contributions = await db.Contributions
            .Where(c => c.MemberId == memberId)
            .ToListAsync(ct);

        if (contributions.Count == 0) return;

        var totalCycles   = contributions.Count;
        var paidOnTime    = contributions.Count(c => c.Status == ContributionStatus.Paid && c.PaidAt <= c.DueDate);
        var completed     = contributions.Count(c => c.Status is ContributionStatus.Paid or ContributionStatus.Overpaid);
        var defaults      = contributions.Count(c => c.Status == ContributionStatus.Defaulted);
        var recovered     = contributions.Count(c => c.Status == ContributionStatus.Defaulted && c.PaidAmount > 0);

        double onTimeRate      = totalCycles > 0 ? (double)paidOnTime / totalCycles : 0;
        double completionRate  = totalCycles > 0 ? (double)completed / totalCycles : 0;
        double defaultRecovery = defaults > 0 ? (double)recovered / defaults : 1;
        double streakBonus     = Math.Min(member.ConsecutiveOnTimeStreak * 2, 20);

        var score = (int)Math.Round(50 + (onTimeRate * 30) + (completionRate * 15) + (defaultRecovery * 5) + streakBonus);
        score = Math.Clamp(score, 0, 100);

        member.CreditScore = score;
        member.CreditTier  = score switch
        {
            >= 85 => "Excellent",
            >= 70 => "Good",
            >= 50 => "Fair",
            _     => "At Risk"
        };

        await db.SaveChangesAsync(ct);
    }
}
