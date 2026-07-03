using System.Text.Json;
using System.Text.Json.Serialization;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Nomba;
using SusuCircle.Api.Common.Persistence;
using SusuCircle.Api.Features.Webhooks.NombaWebhook;

namespace SusuCircle.Api.Infrastructure;

// ══════════════════════════════════════════════════════════════════════════════
// SEAMLESS WEBHOOK FALLBACK — this is the piece that replaces "wait for Nomba
// to call us" with "we go check ourselves, on a schedule."
//
// HOW IT KNOWS THE MEMBER + AMOUNT (your question):
//   It doesn't need to be told. Exactly like a real webhook, the transaction
//   RECORD ITSELF carries both:
//     - the receiving VA number  → looked up against Member.VirtualAccountNumber
//       (same lookup NombaWebhookHandler already does)
//     - the amount               → read directly off the transaction
//   No admin, no test, no manual memberId/amount input anywhere in this path.
//
// WHY IT ISN'T FULLY LIVE YET: the exact JSON field that carries the receiving
// VA number on THIS endpoint (GET /v1/transactions/accounts) is still being
// confirmed (three guessed field names — accountNumber, aliasAccountNumber,
// customerBillerId — didn't match a real response). See ExtractVirtualAccount
// below: once the real field name is confirmed, add it there and this job
// starts auto-reconciling immediately, with ZERO other changes needed.
//
// SAFE DEGRADATION: until that field is known, every transaction that can't be
// matched to a VA is written to UnmatchedTransaction for manual review on the
// existing Match Transactions screen — exactly today's behaviour. Nothing is
// ever guessed or force-matched; a miss here is "one more row to review," not
// a wrongly-credited payment.
//
// CODE REUSE: when a match IS found, this builds a real NombaWebhookPayload
// and sends it through the SAME ProcessWebhookCommand your actual webhook
// endpoint uses — so Paid/Partial/Overpaid math, notifications, admin
// notifications, credit carry-over, and SignalR all run identically whether
// the payment arrived via a real webhook or was discovered by this sweep.
// ══════════════════════════════════════════════════════════════════════════════

public class AutoReconciliationJob(
    AppDbContext db,
    HttpClient http,
    INombaTokenProvider tokenProvider,
    IOptions<NombaOptions> nombaOptions,
    IMediator mediator,
    ILogger<AutoReconciliationJob> logger)
{
    // Overlaps each run with the previous one so a slow transaction never
    // falls through the gap between two scheduled runs.
    private static readonly TimeSpan LookbackWindow = TimeSpan.FromMinutes(15);

    public async Task RunAsync(CancellationToken ct = default)
    {
        var opt = nombaOptions.Value;
        var since = DateTime.UtcNow.Subtract(LookbackWindow);

        List<RawTransaction> transactions;
        try
        {
            transactions = await FetchTransactionsAsync(opt, since, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AutoReconciliationJob: failed to fetch transactions this run.");
            return;
        }

        int autoReconciled = 0, queuedForReview = 0, skippedAlreadyDone = 0;

        foreach (var txn in transactions)
        {
            // CONFIRMED: entryType == "CREDIT" && type == "vact_transfer" precisely
            // identifies genuine inbound virtual-account funding.
            if (!string.Equals(txn.EntryType, "CREDIT", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(txn.Type, "vact_transfer", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var reference = txn.MerchantTxRef ?? txn.Id;
            if (string.IsNullOrWhiteSpace(reference)) continue;

            var alreadyReconciled = await db.Contributions
                .AnyAsync(c => c.NombaTransactionRef == reference || c.NombaTransactionRef == txn.Id, ct);
            var alreadyQueued = await db.UnmatchedTransactions
                .AnyAsync(u => u.TransactionReference == reference, ct);

            if (alreadyReconciled) { skippedAlreadyDone++; continue; }
            if (alreadyQueued) continue;

            // ── CONFIRMED matching, two-tier ──
            // Tier 1 (most precise): virtualAccountReference IS the Member.Id used
            // as accountRef at VA-creation time — a direct, unambiguous FK match.
            // Tier 2 (fallback): recipientAccountNumber against VirtualAccountNumber,
            // for any older members whose VA might predate this reference convention.
            Member? member = null;

            if (Guid.TryParse(txn.VirtualAccountReference, out var memberId))
            {
                member = await db.Members.FirstOrDefaultAsync(m => m.Id == memberId, ct);
            }

            if (member is null && !string.IsNullOrWhiteSpace(txn.RecipientAccountNumber))
            {
                member = await db.Members
                    .FirstOrDefaultAsync(m => m.VirtualAccountNumber == txn.RecipientAccountNumber, ct);
            }

            if (member is null)
            {
                // Genuinely unrecognized — not one of ours (likely another
                // hackathon team on the shared account), or a VA we don't know.
                db.UnmatchedTransactions.Add(new UnmatchedTransaction
                {
                    Id = Guid.NewGuid(),
                    CircleId = Guid.Empty,
                    Amount = txn.Amount,
                    SenderName = txn.SenderName,
                    SenderAccountNumber = txn.AccountNumber,
                    VirtualAccountNumber = txn.RecipientAccountNumber,
                    TransactionReference = reference,
                    ReceivedAt = txn.TimeCreated ?? DateTime.UtcNow,
                    IsResolved = false,
                    CreatedAt = DateTime.UtcNow,
                });
                queuedForReview++;
                continue;
            }

            // ── The seamless part: build a real payload, run it through the
            //    exact same pipeline a genuine webhook would use. ──
            var payload = new NombaWebhookPayload(
                EventType: "payment_success",
                RequestId: txn.Id,
                Data: new NombaWebhookData(
                    Merchant: null,
                    Terminal: null,
                    Transaction: new NombaTransactionDetail(
                        TransactionId: txn.Id,
                        Type: txn.Type,
                        OriginatingFrom: "auto-reconciliation-sweep",
                        Rrn: null,
                        TransactionAmount: txn.Amount,
                        Fee: 0,
                        Time: txn.TimeCreated ?? DateTime.UtcNow,
                        MerchantTxRef: txn.MerchantTxRef,
                        AliasAccountNumber: txn.RecipientAccountNumber,
                        AliasAccountName: member.Name,
                        AliasAccountType: "virtual",
                        SessionId: null),
                    Customer: null));

            try
            {
                var result = await mediator.Send(new ProcessWebhookCommand(payload), ct);
                if (result.Processed) autoReconciled++;
                logger.LogInformation(
                    "AutoReconciliationJob: {Reference} → {Member} — {Message}",
                    reference, member.Name, result.Message);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AutoReconciliationJob: reconciliation failed for {Reference}", reference);
            }
        }

        if (autoReconciled + queuedForReview > 0)
        {
            logger.LogInformation(
                "AutoReconciliationJob run complete: {Scanned} scanned, {Auto} auto-reconciled, {Queued} queued for manual review, {Skipped} already done.",
                transactions.Count, autoReconciled, queuedForReview, skippedAlreadyDone);
        }
    }

    private record TransactionListData(
        [property: JsonPropertyName("results")] List<RawTransaction> Results);

    private record RawTransaction(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("amount")] string? AmountRaw,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("merchantTxRef")] string? MerchantTxRef,
        [property: JsonPropertyName("timeCreated")] DateTime? TimeCreated,
        [property: JsonPropertyName("accountNumber")] string? AccountNumber,       // SENDER's own account
        [property: JsonPropertyName("senderName")] string? SenderName,
        // ── CONFIRMED real fields for VA-credit transactions ──
        [property: JsonPropertyName("recipientAccountNumber")] string? RecipientAccountNumber,
        [property: JsonPropertyName("virtualAccountReference")] string? VirtualAccountReference, // == Member.Id
        [property: JsonPropertyName("entryType")] string? EntryType)
    {
        [JsonIgnore]
        public decimal Amount => decimal.TryParse(AmountRaw, out var a) ? a : 0m;
    }

    private async Task<List<RawTransaction>> FetchTransactionsAsync(
        NombaOptions opt, DateTime since, CancellationToken ct)
    {
        var token = await tokenProvider.GetAccessTokenAsync(ct);
        var url = $"{opt.BaseUrl.TrimEnd('/')}/v1/transactions/accounts" +
                  $"?startDate={since:yyyy-MM-ddTHH:mm:ss}&endDate={DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss}&limit=100";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("accountId", opt.ParentAccountId);
        req.Headers.Add("Authorization", $"Bearer {token}");

        using var resp = await http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Nomba Transactions API failed ({(int)resp.StatusCode}): {raw}");

        var envelope = JsonSerializer.Deserialize<NombaEnvelope<TransactionListData>>(raw,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return envelope?.Data?.Results ?? new List<RawTransaction>();
    }
}