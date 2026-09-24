using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;
using SusuCircle.Api.Common.Services;

namespace SusuCircle.Api.Features.Collections.BackfillCollectionAccounts;

// ══════════════════════════════════════════════════════════════════════════════
// Gives a collection account to circles that predate the feature.
//
// Circles created before collection accounts existed have no account row, so the
// sweep and disburse endpoints 404 on them and the whole pooled-funds flow is
// unavailable. This provisions one through exactly the same path CreateCircle
// uses, leaving a backfilled circle indistinguishable from a new one.
//
// It ALSO reconstructs the ledger debits for payouts that already went out.
// Without that, backfilling then sweeping would credit the pool with historical
// contributions whose money has already left the building, and report a healthy
// balance against an empty pool. Reconstructing debits first means the arithmetic
// stays true: credits minus what was already paid out.
//
// A freshly backfilled circle can therefore show a NEGATIVE balance until its
// contributions are swept in. That is correct, not a bug — it is recording that
// money left before anything was ever pooled. Run the sweep and it nets out.
// ══════════════════════════════════════════════════════════════════════════════

// ── One circle ────────────────────────────────────────────────────────────────

public record BackfillCircleCollectionAccountCommand(Guid CircleId) : IRequest<BackfillCircleResult>;

public record BackfillCircleResult(
    Guid CircleId,
    string CircleName,
    bool Created,
    bool IsPlaceholder,
    Guid CollectionAccountId,
    string AccountNumber,
    string BankName,
    int PayoutDebitsReconstructed,
    decimal Balance);

public class BackfillCircleCollectionAccountHandler(
    AppDbContext db,
    ICollectionAccountService collectionAccounts,
    ILogger<BackfillCircleCollectionAccountHandler> logger)
    : IRequestHandler<BackfillCircleCollectionAccountCommand, BackfillCircleResult>
{
    public async Task<BackfillCircleResult> Handle(BackfillCircleCollectionAccountCommand cmd, CancellationToken ct)
    {
        var circle = await db.Circles
            .FirstOrDefaultAsync(c => c.Id == cmd.CircleId, ct)
            ?? throw new NotFoundException(nameof(Circle), cmd.CircleId);

        var (account, created, reconstructed) =
            await BackfillRunner.RunAsync(db, collectionAccounts, circle, ct);

        var balance = await collectionAccounts.GetBalanceAsync(account.Id, ct);

        logger.LogInformation(
            "Backfill for circle {CircleId}: created={Created}, payout debits reconstructed={Count}, balance={Balance}",
            circle.Id, created, reconstructed, balance);

        return new BackfillCircleResult(
            circle.Id, circle.Name, created, account.IsPlaceholder, account.Id,
            account.AccountNumber, account.BankName, reconstructed, balance);
    }
}

public static class BackfillCircleCollectionAccountEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/circles/{circleId:guid}/collection-account",
            async (Guid circleId, IMediator mediator) =>
            {
                var result = await mediator.Send(new BackfillCircleCollectionAccountCommand(circleId));
                return Results.Ok(ApiResponse<BackfillCircleResult>.Ok(result,
                    result.Created
                        ? "Collection account provisioned."
                        : "Circle already had a live collection account."));
            })
        .WithName("BackfillCircleCollectionAccount")
        .WithTags("Collections");
}

// ── Every circle that needs one ───────────────────────────────────────────────

public record BackfillAllCollectionAccountsCommand : IRequest<BackfillAllResult>;

public record BackfillAllResult(
    int CirclesScanned,
    int Provisioned,
    int AlreadyHadAccount,
    int Failed,
    int PayoutDebitsReconstructed,
    IReadOnlyList<BackfillCircleOutcome> Results);

public record BackfillCircleOutcome(
    Guid CircleId,
    string CircleName,
    string Outcome,
    string? AccountNumber,
    int PayoutDebitsReconstructed,
    string? Error);

public class BackfillAllCollectionAccountsHandler(
    AppDbContext db,
    ICollectionAccountService collectionAccounts,
    ILogger<BackfillAllCollectionAccountsHandler> logger)
    : IRequestHandler<BackfillAllCollectionAccountsCommand, BackfillAllResult>
{
    public async Task<BackfillAllResult> Handle(BackfillAllCollectionAccountsCommand cmd, CancellationToken ct)
    {
        var circles = await db.Circles.OrderBy(c => c.CreatedAt).ToListAsync(ct);

        var outcomes = new List<BackfillCircleOutcome>();
        int provisioned = 0, already = 0, failed = 0, totalReconstructed = 0;

        // Sequential, and one circle's failure never aborts the rest: this makes
        // a provider call per circle, and a partially completed backfill that
        // reports exactly which circles failed is far more useful than an
        // all-or-nothing run that rolls back on the last one.
        foreach (var circle in circles)
        {
            try
            {
                var wasPlaceholder =
                    (await collectionAccounts.FindActiveAsync(circle.Id, ct))?.IsPlaceholder ?? false;

                var (account, created, reconstructed) =
                    await BackfillRunner.RunAsync(db, collectionAccounts, circle, ct);

                totalReconstructed += reconstructed;
                if (created) provisioned++; else already++;

                outcomes.Add(new BackfillCircleOutcome(
                    circle.Id, circle.Name,
                    created ? (wasPlaceholder ? "placeholder-upgraded" : "provisioned") : "already-had-account",
                    account.AccountNumber, reconstructed, null));
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogError(ex, "Backfill failed for circle {CircleId} ({Name})", circle.Id, circle.Name);
                outcomes.Add(new BackfillCircleOutcome(
                    circle.Id, circle.Name, "failed", null, 0, ex.Message));
            }
        }

        logger.LogInformation(
            "Collection account backfill complete: {Scanned} scanned, {Provisioned} provisioned, {Already} already had one, {Failed} failed",
            circles.Count, provisioned, already, failed);

        return new BackfillAllResult(
            circles.Count, provisioned, already, failed, totalReconstructed, outcomes);
    }
}

public static class BackfillAllCollectionAccountsEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/admin/collection-accounts/backfill",
            async (IMediator mediator) =>
            {
                var result = await mediator.Send(new BackfillAllCollectionAccountsCommand());
                return Results.Ok(ApiResponse<BackfillAllResult>.Ok(result,
                    $"{result.Provisioned} collection account(s) provisioned, {result.Failed} failed."));
            })
        .WithName("BackfillAllCollectionAccounts")
        .WithTags("Collections");
}

// ── Shared work ───────────────────────────────────────────────────────────────

internal static class BackfillRunner
{
    // Provisions the account if missing, then reconstructs ledger debits for
    // payouts that already left. Both steps are idempotent, so re-running the
    // backfill is safe and is the intended way to pick up circles that failed
    // on an earlier pass.
    public static async Task<(CollectionAccount Account, bool Created, int Reconstructed)> RunAsync(
        AppDbContext db,
        ICollectionAccountService collectionAccounts,
        Circle circle,
        CancellationToken ct)
    {
        var account = await collectionAccounts.FindActiveAsync(circle.Id, ct);
        var created = false;

        if (account is null)
        {
            account = await collectionAccounts.ProvisionAsync(circle, ct);
            db.CollectionAccounts.Add(account);
            await db.SaveChangesAsync(ct);
            created = true;
        }
        else if (account.IsPlaceholder)
        {
            // Circle creation left a stand-in because the provider was down.
            // Upgrade it IN PLACE rather than inserting a replacement: ledger
            // entries point at this row id, and swapping the row would strand
            // any sweep already raised against the placeholder.
            await collectionAccounts.UpgradePlaceholderAsync(account, circle, ct);
            created = true;
        }

        // Completed payouts definitely left. Processing ones are in flight and
        // would have been debited at initiation had the account existed — if one
        // later fails, the payout_failed webhook reverses it, same as any other.
        var payouts = await db.Payouts
            .Where(p => p.CircleId == circle.Id
                     && (p.Status == PayoutStatus.Completed || p.Status == PayoutStatus.Processing))
            .ToListAsync(ct);

        var reconstructed = 0;
        foreach (var payout in payouts)
        {
            if (await collectionAccounts.RecordPayoutDebitAsync(payout, ct))
                reconstructed++;
        }

        return (account, created, reconstructed);
    }
}
