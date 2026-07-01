using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Features.Reconciliation.GetBoard;

// ── Request ───────────────────────────────────────────────────────────────────

// Powers the main reconciliation screen (images 1 & 2): the stat cards, collection
// progress bar, the All/Paid/Partial/Overdue/Unpaid tabs, and the member table.
public record GetReconciliationBoardQuery(Guid AdminId, Guid CircleId, int? Cycle = null)
    : IRequest<ReconciliationBoardResponse>;

public record ReconciliationBoardResponse(
    Guid CircleId,
    string CircleName,
    string Plan,
    int Cycle,
    decimal ContributionAmount,
    // Stat cards
    decimal Collected,
    decimal Expected,
    int CollectionRate,          // percentage 0-100
    int PaidCount,
    int PartialCount,
    int OverdueCount,
    int UnpaidCount,
    int TotalMembers,
    int AttentionCount,          // partial + overdue
    int OutstandingMembers,      // members who still owe anything
    List<ReconciliationRowDto> Rows);

public record ReconciliationRowDto(
    Guid MemberId,
    string MemberName,
    int PayoutPosition,
    string? VirtualAccountNumber,
    decimal Received,
    decimal Expected,
    decimal Balance,
    string Status,               // Paid | Partial | Unpaid | Overdue
    int? DaysOverdue,            // set when overdue
    DateTime? LastPaymentAt,
    DateTime? DueDate);

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetReconciliationBoardHandler(AppDbContext db)
    : IRequestHandler<GetReconciliationBoardQuery, ReconciliationBoardResponse>
{
    public async Task<ReconciliationBoardResponse> Handle(GetReconciliationBoardQuery q, CancellationToken ct)
    {
        var circle = await db.Circles
            .FirstOrDefaultAsync(c => c.Id == q.CircleId && c.AdminId == q.AdminId, ct)
            ?? throw new NotFoundException(nameof(Circle), q.CircleId);

        var cycle = q.Cycle ?? circle.CurrentCycle;

        var members = await db.Members
            .Where(m => m.CircleId == circle.Id && m.Status == MemberStatus.Active)
            .OrderBy(m => m.PayoutPosition)
            .ToListAsync(ct);

        var memberIds = members.Select(m => m.Id).ToList();

        var contributions = await db.Contributions
            .Where(c => c.CircleId == circle.Id
                && c.CycleNumber == cycle
                && memberIds.Contains(c.MemberId))
            .ToListAsync(ct);

        var byMember = contributions.ToDictionary(c => c.MemberId);
        var now = DateTime.UtcNow;

        var rows = new List<ReconciliationRowDto>();
        decimal collected = 0m;

        foreach (var m in members)
        {
            byMember.TryGetValue(m.Id, out var contrib);

            var expected = contrib?.ExpectedAmount ?? circle.ContributionAmount;
            var paid = contrib?.PaidAmount ?? 0m;
            var credit = contrib?.CreditApplied ?? 0m;
            var balance = expected - credit - paid;
            collected += paid;

            var dueDate = contrib?.DueDate;
            int? daysOverdue = null;

            // Derive display status. Overdue = balance remaining AND past due date.
            string status;
            var rawStatus = contrib?.Status ?? ContributionStatus.Unpaid;

            if (rawStatus is ContributionStatus.Paid or ContributionStatus.Overpaid)
            {
                status = "Paid";
            }
            else if (paid > 0 && balance > 0)
            {
                status = "Partial";
            }
            else
            {
                status = "Unpaid";
            }

            // Layer overdue on top of Partial/Unpaid when past due date
            if (status != "Paid" && dueDate.HasValue && now.Date > dueDate.Value.Date)
            {
                daysOverdue = (now.Date - dueDate.Value.Date).Days;
                if (status == "Unpaid") status = "Overdue";
            }

            rows.Add(new ReconciliationRowDto(
                m.Id,
                m.Name,
                m.PayoutPosition,
                m.VirtualAccountNumber,
                paid,
                expected,
                balance,
                status,
                daysOverdue,
                contrib?.PaidAt,
                dueDate));
        }

        var expectedTotal = circle.ContributionAmount * members.Count;
        var paidCount = rows.Count(r => r.Status == "Paid");
        var partialCount = rows.Count(r => r.Status == "Partial");
        var overdueCount = rows.Count(r => r.DaysOverdue is > 0 && r.Status != "Paid");
        var unpaidCount = rows.Count(r => r.Status is "Unpaid" or "Overdue");
        var rate = expectedTotal > 0 ? (int)Math.Round(collected / expectedTotal * 100) : 0;

        return new ReconciliationBoardResponse(
            circle.Id,
            circle.Name,
            circle.Plan.ToString(),
            cycle,
            circle.ContributionAmount,
            collected,
            expectedTotal,
            rate,
            paidCount,
            partialCount,
            overdueCount,
            unpaidCount,
            members.Count,
            partialCount + overdueCount,
            rows.Count(r => r.Balance > 0),
            rows);
    }
}

// ── Endpoint ──────────────────────────────────────────────────────────────────

public static class GetReconciliationBoardEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/admin/{adminId:guid}/circles/{circleId:guid}/reconciliation",
            async (Guid adminId, Guid circleId, IMediator mediator, int? cycle = null) =>
            {
                var result = await mediator.Send(new GetReconciliationBoardQuery(adminId, circleId, cycle));
                return Results.Ok(ApiResponse<ReconciliationBoardResponse>.Ok(result));
            })
        .WithName("GetReconciliationBoard")
        .WithTags("Reconciliation")
        .AllowAnonymous();
}
