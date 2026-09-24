using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Nomba;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Common.Services;

// ══════════════════════════════════════════════════════════════════════════════
// Shared plumbing for a circle's pooled collection account. Kept in one place
// because provisioning happens at circle creation while balances are read by
// several endpoints, and a second, subtly different balance calculation is
// exactly the kind of thing that ends up disagreeing with itself about money.
// ══════════════════════════════════════════════════════════════════════════════

public interface ICollectionAccountService
{
    // Provisions a circle's collection account with the payment provider.
    // Returns an UNSAVED entity — the caller adds it, so circle creation can
    // commit the circle and its account in a single SaveChanges.
    Task<CollectionAccount> ProvisionAsync(Circle circle, CancellationToken ct = default);

    // Same, but never throws when the provider is unreachable: it returns a
    // clearly-marked placeholder instead. Circle creation uses this, so a
    // provider outage degrades the circle rather than blocking its creation.
    Task<CollectionAccount> ProvisionOrPlaceholderAsync(Circle circle, CancellationToken ct = default);

    // Turns a placeholder into a real account IN PLACE, keeping the same row id
    // so ledger entries already raised against it stay attached. Throws if
    // provisioning still fails, so the caller can report why.
    Task UpgradePlaceholderAsync(CollectionAccount placeholder, Circle circle, CancellationToken ct = default);

    // The circle's one active account. Throws when absent rather than returning
    // null: every circle is provisioned at creation, so a missing account is a
    // real fault and not a case callers should be quietly handling.
    Task<CollectionAccount> GetActiveAsync(Guid circleId, CancellationToken ct = default);

    // Same, but for callers that must tolerate a circle created before
    // collection accounts existed (see the backfill feature).
    Task<CollectionAccount?> FindActiveAsync(Guid circleId, CancellationToken ct = default);

    Task<decimal> GetBalanceAsync(Guid collectionAccountId, CancellationToken ct = default);

    // Records the ledger debit for a payout, so money leaving through the
    // rotation path (TriggerPayout) draws the pool down exactly like a direct
    // disbursement does. Idempotent on the payout id, and a no-op returning
    // false when the circle has no collection account yet.
    Task<bool> RecordPayoutDebitAsync(Payout payout, CancellationToken ct = default);

    // Releases that debit when a payout does not stand — a failed or refunded
    // transfer means the money never left, so the pool must not stay drawn down.
    Task<bool> ReversePayoutDebitAsync(Guid payoutId, CancellationToken ct = default);
}

public class CollectionAccountService(
    AppDbContext db,
    INombaClient payments,
    IConfiguration config,
    ILogger<CollectionAccountService> logger) : ICollectionAccountService
{
    // Ledger reference for a payout debit. Uniquely indexed, so re-recording the
    // same payout is rejected rather than double-debiting the pool.
    public static string PayoutReference(Guid payoutId) => $"PAYOUT-{payoutId}";

    // Deliberately not a plausible NUBAN. Anything reading this value should be
    // unable to mistake it for something a member could actually pay into.
    public const string PlaceholderAccountNumber = "UNPROVISIONED";
    public const string PlaceholderBankName = "Not provisioned";

    public async Task<CollectionAccount> ProvisionAsync(Circle circle, CancellationToken ct = default)
    {
        var provider = config["Payments:Provider"] ?? "Nomba";

        // The circle's own id is the account reference, mirroring how
        // AddMemberHandler uses the member id. That makes the reference the most
        // precise reconciliation key available — inbound transactions carry it
        // back as virtualAccountReference.
        var reference = circle.Id.ToString();
        var accountName = BuildAccountName(circle.Name);

        VirtualAccountResponse va;
        try
        {
            va = await payments.CreateVirtualAccountAsync(new CreateVirtualAccountRequest(
                AccountName: accountName,
                AccountReference: reference,
                CustomerPhone: string.Empty,
                CustomerEmail: null), ct);
        }
        catch (NombaApiException ex)
        {
            logger.LogError(ex, "Collection account provisioning failed for circle {CircleId} ({Name})",
                circle.Id, circle.Name);
            throw new NombaApiException(
                $"Could not provision a collection account for '{circle.Name}': {ex.Message}", ex.StatusCode);
        }

        logger.LogInformation("Collection account {Account} provisioned for circle {CircleId} via {Provider}",
            va.AccountNumber, circle.Id, provider);

        return new CollectionAccount
        {
            Id = Guid.NewGuid(),
            CircleId = circle.Id,
            Provider = provider,
            ExternalAccountId = va.AccountId,
            AccountNumber = va.AccountNumber,
            AccountName = va.AccountName,
            BankName = va.BankName,
            BankCode = va.BankCode,
            IsActive = true,
        };
    }

    public async Task<CollectionAccount> ProvisionOrPlaceholderAsync(Circle circle, CancellationToken ct = default)
    {
        try
        {
            return await ProvisionAsync(circle, ct);
        }
        catch (Exception ex)
        {
            // Swallowed on purpose. The circle is still perfectly usable for
            // everything that does not need a real account, and the placeholder
            // records why so an admin can retry with the backfill endpoint.
            logger.LogError(ex,
                "Collection account could not be provisioned for circle {CircleId} ({Name}); "
                + "falling back to a placeholder. Run the backfill endpoint once the provider is reachable.",
                circle.Id, circle.Name);

            return new CollectionAccount
            {
                Id = Guid.NewGuid(),
                CircleId = circle.Id,
                Provider = config["Payments:Provider"] ?? "Nomba",
                ExternalAccountId = null,
                AccountNumber = PlaceholderAccountNumber,
                AccountName = BuildAccountName(circle.Name),
                BankName = PlaceholderBankName,
                BankCode = string.Empty,
                IsActive = true,
                IsPlaceholder = true,
                ProvisioningError = Truncate(ex.Message, 500),
            };
        }
    }

    public async Task UpgradePlaceholderAsync(
        CollectionAccount placeholder, Circle circle, CancellationToken ct = default)
    {
        // Provision first: if this throws, the placeholder is left exactly as it
        // was rather than half-rewritten.
        var provisioned = await ProvisionAsync(circle, ct);

        placeholder.Provider = provisioned.Provider;
        placeholder.ExternalAccountId = provisioned.ExternalAccountId;
        placeholder.AccountNumber = provisioned.AccountNumber;
        placeholder.AccountName = provisioned.AccountName;
        placeholder.BankName = provisioned.BankName;
        placeholder.BankCode = provisioned.BankCode;
        placeholder.IsPlaceholder = false;
        placeholder.ProvisioningError = null;

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Placeholder collection account for circle {CircleId} upgraded in place to {Account}",
            circle.Id, placeholder.AccountNumber);
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];

    public async Task<CollectionAccount> GetActiveAsync(Guid circleId, CancellationToken ct = default) =>
        await FindActiveAsync(circleId, ct)
        ?? throw new NotFoundException("Active collection account for circle", circleId);

    public async Task<CollectionAccount?> FindActiveAsync(Guid circleId, CancellationToken ct = default) =>
        await db.CollectionAccounts
            .FirstOrDefaultAsync(a => a.CircleId == circleId && a.IsActive, ct);

    // Summed in the database rather than by loading entries — a long-running
    // circle accumulates one row per contribution and per payout.
    public async Task<decimal> GetBalanceAsync(Guid collectionAccountId, CancellationToken ct = default)
    {
        var credits = await db.CollectionLedgerEntries
            .Where(e => e.CollectionAccountId == collectionAccountId
                     && e.Direction == CollectionEntryDirection.Credit)
            .SumAsync(e => (decimal?)e.Amount, ct) ?? 0m;

        var debits = await db.CollectionLedgerEntries
            .Where(e => e.CollectionAccountId == collectionAccountId
                     && e.Direction == CollectionEntryDirection.Debit)
            .SumAsync(e => (decimal?)e.Amount, ct) ?? 0m;

        return credits - debits;
    }

    public async Task<bool> RecordPayoutDebitAsync(Payout payout, CancellationToken ct = default)
    {
        var account = await FindActiveAsync(payout.CircleId, ct);
        if (account is null)
        {
            // A circle created before collection accounts existed. Deliberately
            // not an error: a payout must never fail because bookkeeping is
            // missing. The backfill feature is what closes this.
            logger.LogWarning(
                "Payout {PayoutId} on circle {CircleId} has no collection account — ledger debit skipped. " +
                "Run the collection-account backfill for this circle.",
                payout.Id, payout.CircleId);
            return false;
        }

        var reference = PayoutReference(payout.Id);

        var alreadyRecorded = await db.CollectionLedgerEntries
            .AnyAsync(e => e.Reference == reference, ct);

        if (alreadyRecorded) return false;

        // DisbursedAmount is only populated once a payout actually settles, so
        // fall back to the expected amount for one still in flight.
        var amount = payout.DisbursedAmount > 0 ? payout.DisbursedAmount : payout.ExpectedAmount;

        db.CollectionLedgerEntries.Add(new CollectionLedgerEntry
        {
            Id = Guid.NewGuid(),
            CollectionAccountId = account.Id,
            CircleId = payout.CircleId,
            MemberId = payout.MemberId,
            CycleNumber = payout.CycleNumber,
            Direction = CollectionEntryDirection.Debit,
            Amount = amount,
            PayoutId = payout.Id,
            Reference = reference,
            Description = $"Payout for cycle {payout.CycleNumber}",
        });

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Ledger debit {Amount} recorded for payout {PayoutId} on collection account {Account}",
            amount, payout.Id, account.AccountNumber);

        return true;
    }

    public async Task<bool> ReversePayoutDebitAsync(Guid payoutId, CancellationToken ct = default)
    {
        var reference = PayoutReference(payoutId);

        var entry = await db.CollectionLedgerEntries
            .FirstOrDefaultAsync(e => e.Reference == reference, ct);

        if (entry is null) return false;

        db.CollectionLedgerEntries.Remove(entry);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Ledger debit reversed for payout {PayoutId} — the transfer did not stand", payoutId);
        return true;
    }

    // Providers cap the account name, and some reject punctuation, so the
    // circle's name is trimmed to a predictable "<name> Collections".
    private static string BuildAccountName(string circleName)
    {
        var cleaned = new string(circleName.Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Trim();
        if (cleaned.Length > 30) cleaned = cleaned[..30].Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Susu Collections" : $"{cleaned} Collections";
    }
}
