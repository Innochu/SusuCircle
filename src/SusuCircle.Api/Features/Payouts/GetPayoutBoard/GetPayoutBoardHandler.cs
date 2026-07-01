using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Features.Payouts.GetPayoutBoard;

// ── Request / Response ────────────────────────────────────────────────────────
// Powers the whole Payouts screen in one call:
//   1. CurrentPayout  → the "Current payout" card (recipient, progress, gating)
//   2. PayoutOrder    → the ordered member list (derived from PayoutPosition)
//   3. History        → past payout records from the Payouts table

public record GetPayoutBoardQuery(Guid CircleId) : IRequest<PayoutBoardDto>;

public record PayoutBoardDto(
    Guid CircleId,
    string CircleName,
    string Plan,
    int CurrentCycle,
    int TotalCycles,               // = active member count (one payout each)
    decimal ExpectedPayout,        // ContributionAmount × active members (the pool)
    CurrentPayoutDto? CurrentPayout,
    IEnumerable<PayoutOrderRowDto> PayoutOrder,
    IEnumerable<PayoutHistoryRowDto> History);

public record CurrentPayoutDto(
    Guid MemberId,
    string MemberName,
    int PayoutPosition,
    string? VirtualAccountNumber,
    decimal ExpectedPayout,
    // collection progress for THIS cycle
    int PaidCount,
    int TotalMembers,
    decimal Collected,
    decimal ExpectedTotal,
    bool IsReadyToRelease,         // does the plan's rule say we can pay out?
    bool AlreadyPaid,              // payout for this cycle already completed?
    string GatingMessage);         // e.g. "7 members have not paid yet"

public record PayoutOrderRowDto(
    int Position,
    Guid MemberId,
    string MemberName,
    string? VirtualAccountNumber,
    decimal Amount,                // the pool each member receives on their turn
    string State);                 // "Paid" | "Current" | "Upcoming"

public record PayoutHistoryRowDto(
    Guid Id,
    int CycleNumber,
    string RecipientName,
    decimal ExpectedAmount,
    decimal DisbursedAmount,
    PayoutStatus Status,
    DateTime ScheduledAt,
    DateTime? DisbursedAt);

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetPayoutBoardHandler(AppDbContext db)
    : IRequestHandler<GetPayoutBoardQuery, PayoutBoardDto>
{
    public async Task<PayoutBoardDto> Handle(GetPayoutBoardQuery q, CancellationToken ct)
    {
        var circle = await db.Circles
            .FirstOrDefaultAsync(c => c.Id == q.CircleId, ct)
            ?? throw new NotFoundException(nameof(Circle), q.CircleId);

        // Active members ordered by their payout position — this IS the payout order
        var members = await db.Members
            .Where(m => m.CircleId == circle.Id && m.Status == MemberStatus.Active)
            .OrderBy(m => m.PayoutPosition)
            .ToListAsync(ct);

        var activeCount = members.Count;
        // The pool: what one member receives on their turn.
        var expectedPayout = circle.ContributionAmount * activeCount;

        // Current-cycle contributions (for progress + readiness)
        var cycleContribs = await db.Contributions
            .Where(c => c.CircleId == circle.Id && c.CycleNumber == circle.CurrentCycle)
            .ToListAsync(ct);

        var paidCount = cycleContribs.Count(c =>
            c.Status is ContributionStatus.Paid or ContributionStatus.Overpaid);
        var collected = cycleContribs.Sum(c => c.PaidAmount);
        var expectedTotal = expectedPayout; // same thing: pool for the cycle

        // Existing payouts (history + current-cycle state)
        var payouts = await db.Payouts
            .Include(p => p.Member)
            .Where(p => p.CircleId == circle.Id)
            .ToListAsync(ct);

        var currentCyclePayout = payouts
            .FirstOrDefault(p => p.CycleNumber == circle.CurrentCycle);
        var alreadyPaid = currentCyclePayout?.Status == PayoutStatus.Completed;

        // Plan-specific readiness (mirrors CheckAndTriggerPayoutAsync)
        bool ready = circle.Plan switch
        {
            PlanType.BAM => cycleContribs.Count > 0 && cycleContribs.All(c =>
                c.Status is ContributionStatus.Paid or ContributionStatus.Overpaid),
            PlanType.ADASHI => collected >= expectedTotal,
            _ => false
        };

        // ── Current payout card ──
        CurrentPayoutDto? current = null;
        if (circle.Status == CircleStatus.Active && circle.CurrentCycle <= activeCount && activeCount > 0)
        {
            // Recipient = member at position (CurrentCycle) i.e. skip (cycle-1)
            var recipient = members.Skip(circle.CurrentCycle - 1).FirstOrDefault() ?? members[0];
            var unpaid = activeCount - paidCount;

            var gating = alreadyPaid
                ? $"Payout for cycle {circle.CurrentCycle} has been disbursed."
                : ready
                    ? "All contributions received — ready to release."
                    : circle.Plan == PlanType.BAM
                        ? $"{unpaid} member{(unpaid == 1 ? "" : "s")} have not paid yet. Payout releases when all {activeCount} contributions are received."
                        : $"₦{collected:N0} of ₦{expectedTotal:N0} collected. Payout releases when the full pool is received.";

            current = new CurrentPayoutDto(
                recipient.Id,
                recipient.Name,
                recipient.PayoutPosition,
                recipient.VirtualAccountNumber,
                expectedPayout,
                paidCount,
                activeCount,
                collected,
                expectedTotal,
                ready && !alreadyPaid,
                alreadyPaid,
                gating);
        }

        // ── Payout order (all members, with state overlay) ──
        // A position is "Paid" if a completed payout exists for that cycle number,
        // "Current" if it's this cycle, otherwise "Upcoming".
        var completedCycles = payouts
            .Where(p => p.Status == PayoutStatus.Completed)
            .Select(p => p.CycleNumber)
            .ToHashSet();

        var order = members.Select((m, idx) =>
        {
            var cycleForPosition = idx + 1; // position 1 → cycle 1
            var state = completedCycles.Contains(cycleForPosition) ? "Paid"
                : cycleForPosition == circle.CurrentCycle ? "Current"
                : "Upcoming";

            return new PayoutOrderRowDto(
                m.PayoutPosition,
                m.Id,
                m.Name,
                m.VirtualAccountNumber,
                expectedPayout,
                state);
        }).ToList();

        // ── History (most recent first) ──
        var history = payouts
            .OrderByDescending(p => p.CycleNumber)
            .Select(p => new PayoutHistoryRowDto(
                p.Id, p.CycleNumber, p.Member.Name, p.ExpectedAmount,
                p.DisbursedAmount, p.Status, p.ScheduledAt, p.DisbursedAt))
            .ToList();

        return new PayoutBoardDto(
            circle.Id,
            circle.Name,
            circle.Plan.ToString(),
            circle.CurrentCycle,
            activeCount,
            expectedPayout,
            current,
            order,
            history);
    }
}

// ── Endpoint ──────────────────────────────────────────────────────────────────

public static class GetPayoutBoardEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/circles/{circleId:guid}/payout-board",
            async (Guid circleId, IMediator mediator) =>
            {
                var result = await mediator.Send(new GetPayoutBoardQuery(circleId));
                return Results.Ok(ApiResponse<PayoutBoardDto>.Ok(result));
            })
        .WithName("GetPayoutBoard")
        .WithTags("Payouts")
        .AllowAnonymous();
} 