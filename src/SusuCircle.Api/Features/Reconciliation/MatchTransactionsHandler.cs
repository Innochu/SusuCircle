using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;
using SusuCircle.Api.Common.Services;
using SusuCircle.Api.Infrastructure;

namespace SusuCircle.Api.Features.Reconciliation.Match;

// ── Get unmatched transactions + members side panel ───────────────────────────

public record GetMatchViewQuery(Guid AdminId, Guid CircleId) : IRequest<MatchViewResponse>;

public record MatchViewResponse(
    Guid CircleId,
    string CircleName,
    int UnmatchedCount,
    List<UnmatchedTxnDto> UnmatchedTransactions,
    List<MatchMemberDto> Members);

public record UnmatchedTxnDto(
    Guid Id,
    decimal Amount,
    string? SenderName,
    string? SenderAccountNumber,
    string? VirtualAccountNumber,
    string TransactionReference,
    DateTime ReceivedAt);

public record MatchMemberDto(
    Guid Id,
    string Name,
    string? VirtualAccountNumber,
    string Status);

public class GetMatchViewHandler(AppDbContext db) : IRequestHandler<GetMatchViewQuery, MatchViewResponse>
{
    public async Task<MatchViewResponse> Handle(GetMatchViewQuery q, CancellationToken ct)
    {
        var circle = await db.Circles
            .FirstOrDefaultAsync(c => c.Id == q.CircleId && c.AdminId == q.AdminId, ct)
            ?? throw new NotFoundException(nameof(Circle), q.CircleId);

        var unmatched = await db.UnmatchedTransactions
            .Where(t => t.CircleId == circle.Id && !t.IsResolved)
            .OrderByDescending(t => t.ReceivedAt)
            .Select(t => new UnmatchedTxnDto(
                t.Id, t.Amount, t.SenderName, t.SenderAccountNumber,
                t.VirtualAccountNumber, t.TransactionReference, t.ReceivedAt))
            .ToListAsync(ct);

        var members = await db.Members
            .Where(m => m.CircleId == circle.Id && m.Status == MemberStatus.Active)
            .OrderBy(m => m.PayoutPosition)
            .ToListAsync(ct);

        var memberIds = members.Select(m => m.Id).ToList();
        var contribs = (await db.Contributions
            .Where(c => c.CircleId == circle.Id
                && c.CycleNumber == circle.CurrentCycle
                && memberIds.Contains(c.MemberId))
            .ToListAsync(ct))
            .ToDictionary(c => c.MemberId);

        var now = DateTime.UtcNow;
        var memberDtos = members.Select(m =>
        {
            contribs.TryGetValue(m.Id, out var c);
            string status;
            if (c is null) status = "Unpaid";
            else if (c.Status is ContributionStatus.Paid or ContributionStatus.Overpaid) status = "Paid";
            else if (c.PaidAmount > 0) status = "Partial";
            else if (c.DueDate.Date < now.Date) status = "Overdue";
            else status = "Unpaid";

            return new MatchMemberDto(m.Id, m.Name, m.VirtualAccountNumber, status);
        }).ToList();

        return new MatchViewResponse(
            circle.Id, circle.Name, unmatched.Count, unmatched, memberDtos);
    }
}

// ── Manually match a transaction to a member ──────────────────────────────────

public record MatchTransactionCommand(Guid AdminId, Guid TransactionId, Guid MemberId)
    : IRequest<MatchResult>;

// CHANGED: added receivedAmount, expectedAmount, lastPaymentDate so the
// frontend gets the same field names it already reads off the reconciliation
// board, without a second round-trip to refresh that data after a match.
public record MatchResult(
    bool Matched,
    string Message,
    string ResultingStatus,
    decimal ReceivedAmount,
    decimal ExpectedAmount,
    DateTime? LastPaymentDate);

public class MatchTransactionHandler(
    AppDbContext db,
    INotificationService notifications,
    IHubContext<CircleHub> hub)
    : IRequestHandler<MatchTransactionCommand, MatchResult>
{
    public async Task<MatchResult> Handle(MatchTransactionCommand cmd, CancellationToken ct)
    {
        var txn = await db.UnmatchedTransactions
            .FirstOrDefaultAsync(t => t.Id == cmd.TransactionId && !t.IsResolved, ct)
            ?? throw new NotFoundException("UnmatchedTransaction", cmd.TransactionId);

        var member = await db.Members
            .Include(m => m.Circle)
            .FirstOrDefaultAsync(m => m.Id == cmd.MemberId, ct)
            ?? throw new NotFoundException(nameof(Member), cmd.MemberId);

        if (member.Circle.AdminId != cmd.AdminId)
            throw new NotFoundException(nameof(Member), cmd.MemberId);

        var circle = member.Circle;

        // Idempotency: don't double-apply a reference
        var already = await db.Contributions
            .AnyAsync(c => c.NombaTransactionRef == txn.TransactionReference, ct);
        if (already)
        {
            txn.IsResolved = true;
            await db.SaveChangesAsync(ct);

            // Best-effort: surface the existing contribution's figures even on
            // the "already applied" path, so the response shape stays consistent.
            var existing = await db.Contributions
                .FirstOrDefaultAsync(c => c.NombaTransactionRef == txn.TransactionReference, ct);

            return new MatchResult(
                false, "Transaction reference already applied.", "Unchanged",
                existing?.PaidAmount ?? 0m,
                existing?.ExpectedAmount ?? 0m,
                existing?.PaidAt);
        }

        var contribution = await db.Contributions
            .FirstOrDefaultAsync(c =>
                c.MemberId == member.Id &&
                c.CycleNumber == circle.CurrentCycle, ct);

        if (contribution is null)
        {
            contribution = new Contribution
            {
                Id = Guid.NewGuid(),
                MemberId = member.Id,
                CircleId = circle.Id,
                CycleNumber = circle.CurrentCycle,
                ExpectedAmount = circle.ContributionAmount,
                DueDate = circle.NextContributionDate,
            };
            db.Contributions.Add(contribution);
        }

        contribution.PaidAmount += txn.Amount;
        contribution.NombaTransactionRef = txn.TransactionReference;
        contribution.PaidAt = txn.ReceivedAt;

        var balance = contribution.ExpectedAmount - contribution.CreditApplied - contribution.PaidAmount;

        string status;
        if (balance == 0) { contribution.Status = ContributionStatus.Paid; status = "Paid"; }
        else if (balance > 0) { contribution.Status = ContributionStatus.Partial; status = "Partial"; }
        else { contribution.Status = ContributionStatus.Overpaid; status = "Overpaid"; }

        txn.IsResolved = true;
        txn.MatchedMemberId = member.Id;

        await db.SaveChangesAsync(ct);

        await notifications.SendAsync(member.Id, NotificationType.PaymentReceived,
            "Payment matched ✅",
            $"₦{txn.Amount:N0} matched to your cycle {circle.CurrentCycle} contribution.", ct);

        await hub.Clients.Group(circle.Id.ToString())
            .SendAsync("ContributionUpdated", new
            {
                circleId = circle.Id,
                memberId = member.Id,
                memberName = member.Name,
                cycleNumber = circle.CurrentCycle,
                status,
                paidAmount = contribution.PaidAmount,
                balance,
            }, ct);

        return new MatchResult(
            true, "Transaction matched successfully.", status,
            contribution.PaidAmount,
            contribution.ExpectedAmount,
            contribution.PaidAt);
    }
}

// ── Endpoints ─────────────────────────────────────────────────────────────────

public static class MatchTransactionEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/{adminId:guid}/circles/{circleId:guid}/reconciliation/match",
            async (Guid adminId, Guid circleId, IMediator mediator) =>
            {
                var result = await mediator.Send(new GetMatchViewQuery(adminId, circleId));
                return Results.Ok(ApiResponse<MatchViewResponse>.Ok(result));
            })
            .WithName("GetMatchView")
            .WithTags("Reconciliation")
            .AllowAnonymous();

        app.MapPost("/api/admin/{adminId:guid}/reconciliation/match",
            async (Guid adminId, MatchTransactionRequest body, IMediator mediator) =>
            {
                var result = await mediator.Send(
                    new MatchTransactionCommand(adminId, body.TransactionId, body.MemberId));
                return Results.Ok(ApiResponse<MatchResult>.Ok(result));
            })
            .WithName("MatchTransaction")
            .WithTags("Reconciliation")
            .AllowAnonymous();
    }
}

public record MatchTransactionRequest(Guid TransactionId, Guid MemberId);