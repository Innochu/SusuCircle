using System.Security.Cryptography;
using System.Text;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Nomba;

namespace SusuCircle.Api.Common.Payments;

// ══════════════════════════════════════════════════════════════════════════════
// In-process payment provider. Issues simulated virtual accounts so the full
// app loop — add member → contribute → reconcile → payout — is demonstrable
// with NO external provider at all.
//
// WHY THIS EXISTS: every Nigerian PSP gates persistent virtual account issuance
// behind business registration plus a per-feature activation (Nomba: live
// credentials; Paystack: "Dedicated NUBAN is not available for your business").
// That is a CBN KYC requirement on account creation, not a vendor quirk, so
// there is no provider that hands out real NUBANs to an unverified account.
// This keeps the product demonstrable while that approval is in progress.
//
// IT MOVES NO MONEY. Account numbers are synthetic and belong to no bank;
// transfers are recorded as successful without anything being disbursed.
// Selected via "Payments:Provider" = "Stub", which defaults off — see
// NombaServiceExtensions. Switching to a real provider later is a one-value
// config change, because this implements the same INombaClient as the others.
// ══════════════════════════════════════════════════════════════════════════════

public class StubPaymentClient(ILogger<StubPaymentClient> logger) : INombaClient
{
    // Continues the 8811-prefixed series the existing demo members already use,
    // so simulated accounts look consistent alongside them in the UI.
    private const string AccountPrefix = "8811";
    private const string BankName = "Susu Test Bank (simulated)";
    private const string BankCode = "000000";

    // ── Create virtual account ──────────────────────────────────────────────────
    public Task<VirtualAccountResponse> CreateVirtualAccountAsync(
        CreateVirtualAccountRequest request, CancellationToken ct = default)
    {
        var accountNumber = DeriveAccountNumber(request.AccountReference);

        logger.LogWarning(
            "STUB PROVIDER: issued SIMULATED virtual account {AccountNumber} for {Name} ({Ref}). " +
            "No real account exists and no money can be received.",
            accountNumber, request.AccountName, request.AccountReference);

        return Task.FromResult(new VirtualAccountResponse(
            AccountId: request.AccountReference,
            AccountNumber: accountNumber,
            AccountName: request.AccountName,
            BankName: BankName,
            BankCode: BankCode));
    }

    // ── Initiate bank transfer (payouts) ────────────────────────────────────────
    // Reports SUCCESS immediately. Real providers settle asynchronously via a
    // payout webhook, so a payout here completes in one step where production
    // would sit PENDING until the provider confirms. That difference is the
    // point at which stub and real behaviour diverge most — worth remembering
    // when demoing the payout screen.
    public Task<TransferResponse> InitiateTransferAsync(
        InitiateTransferRequest request, CancellationToken ct = default)
    {
        logger.LogWarning(
            "STUB PROVIDER: SIMULATED transfer of NGN{Amount:N2} to {Account}/{Bank} ref {Ref}. " +
            "NO MONEY HAS MOVED.",
            request.Amount, request.AccountNumber, request.BankCode, request.Reference);

        return Task.FromResult(new TransferResponse(
            TransferReference: request.Reference,
            Status: "SUCCESS"));
    }

    // ── Webhook signature verification ──────────────────────────────────────────
    // Always rejects. In stub mode no real provider is delivering webhooks, so
    // anything arriving at /api/webhooks/* is either a stale provider config or
    // an unsolicited request — neither should be allowed to write contributions.
    // Simulated payments go through POST /api/dev/simulate-webhook instead,
    // which dispatches ProcessWebhookCommand directly and needs no signature.
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        logger.LogWarning(
            "STUB PROVIDER: rejected an inbound webhook. Stub mode delivers no real webhooks — " +
            "use POST /api/dev/simulate-webhook to simulate a payment.");

        return false;
    }

    // ── Bank account lookup ─────────────────────────────────────────────────────
    // Validates shape only — there is no bank to ask. Keeps the self-service
    // payout-account flow exercisable (a malformed number is still rejected)
    // without pretending the account was actually verified.
    public Task<BankLookupResult> LookupBankAccountAsync(
        string accountNumber, string bankCode, CancellationToken ct = default)
    {
        var digits = new string((accountNumber ?? string.Empty).Where(char.IsDigit).ToArray());

        if (digits.Length != 10)
            throw new NombaApiException("Account number must be 10 digits.", 400);

        logger.LogWarning("STUB PROVIDER: SIMULATED lookup for {Account} — not verified with any bank.", digits);

        return Task.FromResult(new BankLookupResult(digits, "Simulated Account Holder"));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    // Deterministic, so re-provisioning the same member always yields the same
    // number — the operation stays idempotent exactly as a real provider's
    // accountRef-keyed creation is. SHA-256 rather than GetHashCode() because
    // .NET string hashing is randomised per process, which would hand the same
    // member a different account number after every restart.
    //
    // NOTE: 6 derived digits is a 1-in-a-million space, and nothing here checks
    // the database for a collision. That is acceptable for a demo of this size
    // but would need a uniqueness check before anyone relied on it.
    internal static string DeriveAccountNumber(string accountReference)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(accountReference ?? string.Empty));

        // Take 4 bytes as an unsigned value, then fold into 6 decimal digits.
        var value = BitConverter.ToUInt32(hash, 0) % 1_000_000;

        return AccountPrefix + value.ToString("D6");
    }
}
