using System.Text.Json;
using System.Text.Json.Serialization;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Nomba;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Features.Reconciliation.sweep;

// ══════════════════════════════════════════════════════════════════════════════
// RECONCILIATION SWEEP — the "Transactions API" key API for this challenge,
// used as a defense-in-depth safety net against missed webhooks.
//
// WHY THIS EXISTS: reconciliation so far depends entirely on Nomba's webhook
// arriving. If it doesn't (exactly what happened during this build — a sandbox
// transfer sat in PENDING_BILLING with no webhook ever delivered), nothing
// catches the payment. This sweep periodically asks Nomba directly "what
// transactions actually happened on this account," independent of webhook
// delivery, and cross-checks against what's already reconciled.
//
// DESIGN DECISION — surface, don't auto-apply: Nomba's public docs example for
// GET /v1/transactions/accounts shows a POS-withdrawal transaction; the exact
// field that carries a virtual-account credit's RECEIVING account number for
// this specific list endpoint isn't confirmed from available documentation
// (the webhook payload uses aliasAccountNumber, but this is a different
// endpoint and may shape it differently). Rather than guess a field name and
// risk silently reconciling a payment to the wrong member, this sweep writes
// candidate matches into UnmatchedTransaction — the SAME table and admin
// screen already built for webhook-missed payments — so a human confirms the
// match. Once you've inspected one real sandbox response and confirmed the
// actual field name, TryExtractAccountNumber below can be tightened to
// auto-apply for anything unambiguous.
// ══════════════════════════════════════════════════════════════════════════════

public record RunReconciliationSweepCommand(Guid AdminId, Guid CircleId, DateTime? Since = null)
    : IRequest<SweepResult>;

public record SweepResult(
    int TransactionsScanned,
    int AlreadyReconciled,
    int NewCandidatesFound,
    List<string> CandidateReferences);

public class RunReconciliationSweepHandler(
    AppDbContext db,
    HttpClient http,
    INombaTokenProvider tokenProvider,
    IOptions<NombaOptions> nombaOptions,
    ILogger<RunReconciliationSweepHandler> logger)
    : IRequestHandler<RunReconciliationSweepCommand, SweepResult>
{
    public async Task<SweepResult> Handle(RunReconciliationSweepCommand cmd, CancellationToken ct)
    {
        var circle = await db.Circles
            .FirstOrDefaultAsync(c => c.Id == cmd.CircleId && c.AdminId == cmd.AdminId, ct)
            ?? throw new NotFoundException(nameof(Circle), cmd.CircleId);

        var members = await db.Members
            .Where(m => m.CircleId == circle.Id && m.VirtualAccountNumber != null)
            .ToListAsync(ct);

        var vaLookup = members
            .Where(m => m.VirtualAccountNumber != null)
            .ToDictionary(m => m.VirtualAccountNumber!, m => m);

        var since = cmd.Since ?? DateTime.UtcNow.AddDays(-7);
        var opt = nombaOptions.Value;

        // ── Call Nomba's Transactions API directly ──
        var token = await tokenProvider.GetAccessTokenAsync(ct);
        var url = $"{opt.BaseUrl.TrimEnd('/')}/v1/transactions/accounts?startDate={since:yyyy-MM-ddTHH:mm:ss}&endDate={DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss}&limit=100";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("accountId", opt.ParentAccountId);
        req.Headers.Add("Authorization", $"Bearer {token}");

        using var resp = await http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);

        // TEMPORARY — logs the full raw Transactions API response so we can find
        // the real field name that carries the receiving VA number for a VA-credit
        // transaction (the three guessed field names in TryFindKnownVirtualAccount
        // didn't match anything real). Search Render logs for a known reference
        // (e.g. "39b1fb22") to see its actual surrounding JSON fields, then update
        // TryFindKnownVirtualAccount accordingly. REMOVE once confirmed — this
        // logs full transaction data including references across the whole
        // shared hackathon account, not just your own.
        logger.LogWarning("RAW NOMBA TRANSACTIONS RESPONSE: {Body}", raw);

        if (!resp.IsSuccessStatusCode)
        {
            logger.LogError("Reconciliation sweep: Nomba Transactions API failed {Status}: {Body}", resp.StatusCode, raw);
            throw new NombaApiException($"Failed to fetch transactions ({(int)resp.StatusCode}).");
        }

        var envelope = JsonSerializer.Deserialize<NombaEnvelope<TransactionListData>>(raw,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var transactions = envelope?.Data?.Results ?? new List<RawTransaction>();
        var candidates = new List<string>();
        int alreadyReconciled = 0;

        foreach (var txn in transactions)
        {
            // Only interested in inbound credits — filter out withdrawals, POS,
            // airtime, etc. "type" values here are unconfirmed for this endpoint;
            // this list is a best-effort filter, refine once real data is seen.
            if (txn.Type is not null &&
                !txn.Type.Contains("credit", StringComparison.OrdinalIgnoreCase) &&
                !txn.Type.Contains("transfer", StringComparison.OrdinalIgnoreCase) &&
                !txn.Type.Contains("virtual", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var reference = txn.MerchantTxRef ?? txn.Id;
            if (string.IsNullOrWhiteSpace(reference)) continue;

            // Skip anything already reconciled via webhook or manual match.
            var alreadyInContributions = await db.Contributions
                .AnyAsync(c => c.NombaTransactionRef == reference || c.NombaTransactionRef == txn.Id, ct);
            var alreadyUnmatched = await db.UnmatchedTransactions
                .AnyAsync(u => u.TransactionReference == reference, ct);

            if (alreadyInContributions)
            {
                alreadyReconciled++;
                continue;
            }
            if (alreadyUnmatched) continue;

            // Best-effort: does this transaction mention any of our members' VA
            // numbers anywhere in the raw payload? (Field name unconfirmed — see
            // header comment — so we scan generously rather than assume one field.)
            var matchedVa = TryFindKnownVirtualAccount(txn, vaLookup.Keys);

            db.UnmatchedTransactions.Add(new UnmatchedTransaction
            {
                Id = Guid.NewGuid(),
                CircleId = circle.Id,
                Amount = txn.Amount,
                SenderName = null,
                SenderAccountNumber = null,
                VirtualAccountNumber = matchedVa,
                TransactionReference = reference,
                ReceivedAt = txn.TimeCreated ?? DateTime.UtcNow,
                IsResolved = false,
                CreatedAt = DateTime.UtcNow,
            });

            candidates.Add(reference);
        }

        if (candidates.Count > 0)
            await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Reconciliation sweep for circle {CircleId}: {Scanned} scanned, {Reconciled} already reconciled, {New} new candidates queued for review",
            circle.Id, transactions.Count, alreadyReconciled, candidates.Count);

        return new SweepResult(transactions.Count, alreadyReconciled, candidates.Count, candidates);
    }

    // Scans known fields for a match against any of this circle's member VAs.
    // Checks multiple plausible field names since the exact one isn't confirmed.
    private static string? TryFindKnownVirtualAccount(RawTransaction txn, IEnumerable<string> knownVas)
    {
        var candidates = new[] { txn.AccountNumber, txn.AliasAccountNumber, txn.CustomerBillerId };
        foreach (var va in knownVas)
        {
            if (candidates.Any(c => c is not null && c.Contains(va)))
                return va;
        }
        return null;
    }

    // ── Response shapes (confirmed fields per docs; a few speculative extras
    //    included defensively since VA-credit-type transactions weren't shown
    //    in the available example) ──
    private record TransactionListData(
        [property: JsonPropertyName("results")] List<RawTransaction> Results,
        [property: JsonPropertyName("cursor")] string? Cursor);

    private record RawTransaction(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("amount")] string? AmountRaw,   // Nomba sends this as a STRING, e.g. "100.00"
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("merchantTxRef")] string? MerchantTxRef,
        [property: JsonPropertyName("timeCreated")] DateTime? TimeCreated,
        [property: JsonPropertyName("customerBillerId")] string? CustomerBillerId,
        // Speculative — not confirmed present on this endpoint's response:
        [property: JsonPropertyName("accountNumber")] string? AccountNumber,
        [property: JsonPropertyName("aliasAccountNumber")] string? AliasAccountNumber)
    {
        [JsonIgnore]
        public decimal Amount => decimal.TryParse(AmountRaw, out var a) ? a : 0m;
    }
}

// ── Endpoint ──────────────────────────────────────────────────────────────────

public record RunSweepRequest(DateTime? Since = null);

public static class RunReconciliationSweepEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/admin/{adminId:guid}/circles/{circleId:guid}/reconciliation/sweep",
            async (Guid adminId, Guid circleId, IMediator mediator, RunSweepRequest? body) =>
            {
                var result = await mediator.Send(
                    new RunReconciliationSweepCommand(adminId, circleId, body?.Since));
                return Results.Ok(ApiResponse<SweepResult>.Ok(result,
                    "Sweep complete. New candidates are queued in Match Transactions for review."));
            })
        .WithName("RunReconciliationSweep")
        .WithTags("Reconciliation")
        .AllowAnonymous();
}