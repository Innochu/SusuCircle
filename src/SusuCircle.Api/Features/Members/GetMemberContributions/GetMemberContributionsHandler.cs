using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Features.Members.GetMemberContributions;

// ── Request / Response ────────────────────────────────────────────────────────
// Powers the member portal Contributions tab: stat tiles, this-cycle circle
// progress, and the member's own full payment history table.
//
// ROUTE CHANGED: /contributions -> /contributions/summary. Your existing
// GetContributionsEndpoint (Features/Contributions/GetContributions/) already
// owns the plain "/api/members/{memberId}/contributions" route (a raw,
// authenticated per-cycle list) — this endpoint returns a different, richer
// shape (stat tiles + circle-wide progress) for the member portal UI, so it
// needed its own path rather than colliding with that one.

public record GetMemberContributionsQuery(Guid MemberId) : IRequest<MemberContributionsDto>;

public record MemberContributionsDto(
    Guid MemberId,
    string CircleName,

    // Stat tiles
    int TotalPayments,             // cycles with any PaidAmount > 0
    int OnTimeCount,
    int ResolvedCycleCount,        // Paid/Overpaid/Defaulted — cycles with a final outcome
    int OnTimeRatePercent,
    decimal TotalContributed,

    // This-cycle circle-wide progress bar
    int CirclePaidCount,
    int CircleTotalMembers,
    int CircleCollectionRatePercent,

    List<MemberContributionRowDto> History);

public record MemberContributionRowDto(
    int CycleNumber,
    DateTime? DatePaid,
    DateTime DueDate,
    string Status,                 // Paid | Partial | Overpaid | Unpaid | Overdue
    decimal Amount);

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetMemberContributionsHandler(AppDbContext db)
    : IRequestHandler<GetMemberContributionsQuery, MemberContributionsDto>
{
    public async Task<MemberContributionsDto> Handle(GetMemberContributionsQuery q, CancellationToken ct)
    {
        var member = await db.Members
            .Include(m => m.Circle)
            .FirstOrDefaultAsync(m => m.Id == q.MemberId, ct)
            ?? throw new NotFoundException(nameof(Member), q.MemberId);

        var circle = member.Circle;
        var now = DateTime.UtcNow;

        var contributions = await db.Contributions
            .Where(c => c.MemberId == member.Id)
            .OrderBy(c => c.CycleNumber)
            .ToListAsync(ct);

        var rows = new List<MemberContributionRowDto>();
        foreach (var c in contributions)
        {
            var isPastDue = now.Date > c.DueDate.Date;
            string status = c.Status switch
            {
                ContributionStatus.Paid => "Paid",
                ContributionStatus.Overpaid => "Overpaid",
                ContributionStatus.Partial => "Partial",
                _ when isPastDue => "Overdue",
                _ => "Unpaid",
            };

            rows.Add(new MemberContributionRowDto(
                c.CycleNumber, c.PaidAt, c.DueDate, status, c.PaidAmount));
        }

        var totalPayments = contributions.Count(c => c.PaidAmount > 0);
        var resolved = rows.Where(r => r.Status is "Paid" or "Overpaid").ToList();
        var onTime = contributions.Count(c =>
            c.Status is ContributionStatus.Paid or ContributionStatus.Overpaid &&
            c.PaidAt.HasValue && c.PaidAt.Value <= c.DueDate);
        var onTimeRate = resolved.Count > 0 ? (int)Math.Round((double)onTime / resolved.Count * 100) : 0;
        var totalContributed = contributions.Sum(c => c.PaidAmount);

        // This cycle's circle-wide progress
        var cycleContribs = await db.Contributions
            .Where(c => c.CircleId == circle.Id && c.CycleNumber == circle.CurrentCycle)
            .ToListAsync(ct);
        var circleTotalMembers = await db.Members
            .CountAsync(m => m.CircleId == circle.Id && m.Status == MemberStatus.Active, ct);
        var circlePaidCount = cycleContribs.Count(c =>
            c.Status is ContributionStatus.Paid or ContributionStatus.Overpaid);
        var circleRate = circleTotalMembers > 0
            ? (int)Math.Round((double)circlePaidCount / circleTotalMembers * 100)
            : 0;

        return new MemberContributionsDto(
            member.Id,
            circle.Name,
            totalPayments,
            onTime,
            resolved.Count,
            onTimeRate,
            totalContributed,
            circlePaidCount,
            circleTotalMembers,
            circleRate,
            rows);
    }
}

// ── Endpoint ──────────────────────────────────────────────────────────────────

public static class GetMemberContributionsEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/members/{memberId:guid}/contributions/summary",
            async (Guid memberId, IMediator mediator) =>
            {
                var result = await mediator.Send(new GetMemberContributionsQuery(memberId));
                return Results.Ok(ApiResponse<MemberContributionsDto>.Ok(result));
            })
        .WithName("GetMemberContributionsSummary")
        .WithTags("Members")
        .AllowAnonymous();
}