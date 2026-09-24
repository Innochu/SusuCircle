using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;
using SusuCircle.Api.Common.Services;

namespace SusuCircle.Api.Features.Collections.SweepContributions;

// ══════════════════════════════════════════════════════════════════════════════
// "Debit every member's virtual account, credit the circle's collection account."
//
// READ THIS BEFORE CHANGING IT. No money moves here, and none can. A provider
// virtual account is an inbound-only rail: the moment a member pays into theirs,
// the provider routes those funds into the merchant parent wallet automatically
// (this is the same behaviour documented in TriggerPayoutHandler, and the reason
// payouts must target a real external bank account). There is no debit-a-VA
// operation on INombaClient because the rail does not offer one.
//
// So the sweep is a LEDGER move over funds that are already pooled upstream: it
// credits the circle's collection account with every contribution that has been
// reconciled but not yet attributed to the pool. That is what makes the circle's
// balance a meaningful number to pay out of.
//
// Idempotent by construction: each credit is keyed "SWEEP-{contributionId}" on a
// uniquely indexed column, so re-running the sweep credits nothing twice — which
// matters, because this is the kind of endpoint people retry when it looks slow.
// ══════════════════════════════════════════════════════════════════════════════

public record SweepContributionsCommand(Guid CircleId) : IRequest<SweepContributionsResult>;

public record SweepContributionsResult(
    Guid CircleId,
    Guid CollectionAccountId,
    string CollectionAccountNumber,
    string CollectionBankName,
    int ContributionsSwept,
    decimal TotalCredited,
    decimal BalanceAfter,
    IReadOnlyList<SweptContributionDto> Swept);

public record SweptContributionDto(
    Guid MemberId,
    string MemberName,
    string? MemberVirtualAccountNumber,
    Guid ContributionId,
    int CycleNumber,
    decimal Amount);

public class SweepContributionsValidator : AbstractValidator<SweepContributionsCommand>
{
    public SweepContributionsValidator() => RuleFor(x => x.CircleId).NotEmpty();
}

public class SweepContributionsHandler(
    AppDbContext db,
    ICollectionAccountService collectionAccounts,
    ILogger<SweepContributionsHandler> logger)
    : IRequestHandler<SweepContributionsCommand, SweepContributionsResult>
{
    public async Task<SweepContributionsResult> Handle(SweepContributionsCommand cmd, CancellationToken ct)
    {
        var circle = await db.Circles
            .FirstOrDefaultAsync(c => c.Id == cmd.CircleId, ct)
            ?? throw new NotFoundException(nameof(Circle), cmd.CircleId);

        var account = await collectionAccounts.GetActiveAsync(circle.Id, ct);

        // Every contribution carrying real money, across all cycles — not just
        // the current one. Restricting to the open cycle would strand money from
        // a cycle that closed before anyone ran a sweep.
        var paid = await db.Contributions
            .Where(c => c.CircleId == circle.Id && c.PaidAmount > 0)
            .Join(db.Members, c => c.MemberId, m => m.Id, (c, m) => new
            {
                Contribution = c,
                MemberName = m.Name,
                MemberVa = m.VirtualAccountNumber,
            })
            .ToListAsync(ct);

        if (paid.Count == 0)
        {
            var emptyBalance = await collectionAccounts.GetBalanceAsync(account.Id, ct);
            return Build(circle, account, [], emptyBalance);
        }

        // Which of those are already in the ledger. Checked against the unique
        // reference rather than a flag on Contribution, so the ledger stays the
        // single source of truth about what has been swept.
        var references = paid.Select(p => SweepReference(p.Contribution.Id)).ToList();
        var alreadySwept = await db.CollectionLedgerEntries
            .Where(e => references.Contains(e.Reference))
            .Select(e => e.Reference)
            .ToListAsync(ct);

        var alreadySweptSet = alreadySwept.ToHashSet();

        var newEntries = new List<CollectionLedgerEntry>();
        var swept = new List<SweptContributionDto>();

        foreach (var row in paid)
        {
            var reference = SweepReference(row.Contribution.Id);
            if (alreadySweptSet.Contains(reference)) continue;

            newEntries.Add(new CollectionLedgerEntry
            {
                Id = Guid.NewGuid(),
                CollectionAccountId = account.Id,
                CircleId = circle.Id,
                MemberId = row.Contribution.MemberId,
                CycleNumber = row.Contribution.CycleNumber,
                Direction = CollectionEntryDirection.Credit,
                Amount = row.Contribution.PaidAmount,
                ContributionId = row.Contribution.Id,
                Reference = reference,
                Description = $"Contribution swept from {row.MemberName} (cycle {row.Contribution.CycleNumber})",
            });

            swept.Add(new SweptContributionDto(
                row.Contribution.MemberId, row.MemberName, row.MemberVa,
                row.Contribution.Id, row.Contribution.CycleNumber, row.Contribution.PaidAmount));
        }

        if (newEntries.Count > 0)
        {
            db.CollectionLedgerEntries.AddRange(newEntries);
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Swept {Count} contributions totalling {Total} into collection account {Account} for circle {CircleId}",
                newEntries.Count, newEntries.Sum(e => e.Amount), account.AccountNumber, circle.Id);
        }

        var balance = await collectionAccounts.GetBalanceAsync(account.Id, ct);
        return Build(circle, account, swept, balance);
    }

    private static string SweepReference(Guid contributionId) => $"SWEEP-{contributionId}";

    private static SweepContributionsResult Build(
        Circle circle, CollectionAccount account, IReadOnlyList<SweptContributionDto> swept, decimal balance) =>
        new(circle.Id, account.Id, account.AccountNumber, account.BankName,
            swept.Count, swept.Sum(s => s.Amount), balance, swept);
}

public static class SweepContributionsEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/circles/{circleId:guid}/collection/sweep",
            async (Guid circleId, IMediator mediator) =>
            {
                var result = await mediator.Send(new SweepContributionsCommand(circleId));
                return Results.Ok(ApiResponse<SweepContributionsResult>.Ok(result,
                    $"{result.ContributionsSwept} contribution(s) credited to the collection account."));
            })
        .WithName("SweepContributionsToCollection")
        .WithTags("Collections");
}
