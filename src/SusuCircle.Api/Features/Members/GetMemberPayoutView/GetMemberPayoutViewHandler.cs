using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Features.Members.GetMemberPayoutView;

// ── Request / Response ────────────────────────────────────────────────────────
// Member-facing mirror of the admin payout board (GetPayoutBoardHandler) —
// same underlying data, reshaped around "where do I stand" rather than
// "manage this circle's payouts." Each queue row flags IsRequestingMember so
// the frontend can render "You" / highlight without string-matching names.

public record GetMemberPayoutViewQuery(Guid MemberId) : IRequest<MemberPayoutViewDto>;

public record MemberPayoutViewDto(
    Guid MemberId,
    int MyPosition,
    string MyStatus,               // "ThisCyclesTurn" | "Pending" | "Completed"
    decimal ExpectedPayout,
    int CirclePaidCount,
    int CircleTotalMembers,
    int CircleCollectionRatePercent,
    List<MemberPayoutQueueRowDto> PayoutQueue,
    List<MemberPayoutHistoryRowDto> History);

public record MemberPayoutQueueRowDto(
    int Position,
    Guid MemberId,
    string MemberName,
    bool IsRequestingMember,
    string Status,                 // "Completed" | "ThisCyclesTurn" | "Pending" | "Failed"
    decimal Amount);

public record MemberPayoutHistoryRowDto(
    int CycleNumber,
    decimal DisbursedAmount,
    string Status,
    DateTime? DisbursedAt);

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetMemberPayoutViewHandler(AppDbContext db)
    : IRequestHandler<GetMemberPayoutViewQuery, MemberPayoutViewDto>
{
    public async Task<MemberPayoutViewDto> Handle(GetMemberPayoutViewQuery q, CancellationToken ct)
    {
        var member = await db.Members
            .Include(m => m.Circle)
            .FirstOrDefaultAsync(m => m.Id == q.MemberId, ct)
            ?? throw new NotFoundException(nameof(Member), q.MemberId);

        var circle = member.Circle;

        var members = await db.Members
            .Where(m => m.CircleId == circle.Id && m.Status == MemberStatus.Active)
            .OrderBy(m => m.PayoutPosition)
            .ToListAsync(ct);

        var activeCount = members.Count;
        var expectedPayout = circle.ContributionAmount * activeCount;

        var cycleContribs = await db.Contributions
            .Where(c => c.CircleId == circle.Id && c.CycleNumber == circle.CurrentCycle)
            .ToListAsync(ct);
        var paidCount = cycleContribs.Count(c =>
            c.Status is ContributionStatus.Paid or ContributionStatus.Overpaid);
        var rate = activeCount > 0 ? (int)Math.Round((double)paidCount / activeCount * 100) : 0;

        var payouts = await db.Payouts
            .Where(p => p.CircleId == circle.Id)
            .ToListAsync(ct);
        var completedCycles = payouts
            .Where(p => p.Status == PayoutStatus.Completed)
            .Select(p => p.CycleNumber)
            .ToHashSet();

        var queue = members.Select((m, idx) =>
        {
            var cycleForPosition = idx + 1;
            string status = completedCycles.Contains(cycleForPosition) ? "Completed"
                : cycleForPosition == circle.CurrentCycle ? "ThisCyclesTurn"
                : "Pending";

            return new MemberPayoutQueueRowDto(
                m.PayoutPosition, m.Id, m.Name, m.Id == member.Id, status, expectedPayout);
        }).ToList();

        var myRow = queue.FirstOrDefault(r => r.IsRequestingMember);
        var myStatus = myRow?.Status ?? "Pending";

        var history = payouts
            .Where(p => p.MemberId == member.Id)
            .OrderByDescending(p => p.CycleNumber)
            .Select(p => new MemberPayoutHistoryRowDto(
                p.CycleNumber, p.DisbursedAmount, p.Status.ToString(), p.DisbursedAt))
            .ToList();

        return new MemberPayoutViewDto(
            member.Id,
            member.PayoutPosition,
            myStatus,
            expectedPayout,
            paidCount,
            activeCount,
            rate,
            queue,
            history);
    }
}

// ── Endpoint ──────────────────────────────────────────────────────────────────

public static class GetMemberPayoutViewEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/members/{memberId:guid}/payout",
            async (Guid memberId, IMediator mediator) =>
            {
                var result = await mediator.Send(new GetMemberPayoutViewQuery(memberId));
                return Results.Ok(ApiResponse<MemberPayoutViewDto>.Ok(result));
            })
        .WithName("GetMemberPayoutView")
        .WithTags("Members")
        .AllowAnonymous();
}