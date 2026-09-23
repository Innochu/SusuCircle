using System.Text.Json;
using System.Text.Json.Serialization;
using MediatR;
using SusuCircle.Api.Common.Nomba;
using SusuCircle.Api.Features.Webhooks.NombaWebhook;

namespace SusuCircle.Api.Features.Webhooks.PaystackWebhook;

// ══════════════════════════════════════════════════════════════════════════════
// Paystack webhook receiver.
//
// This endpoint deliberately does NOT reimplement reconciliation. It translates
// Paystack's payload into the NombaWebhookPayload shape and hands it to the
// existing ProcessWebhookCommand, so the reconciliation engine — partial/overpaid
// handling, credit rollover, streaks, payout triggering, SignalR, notifications —
// stays a single implementation with one set of semantics to reason about.
//
// Event mapping:
//   charge.success (channel: dedicated_nuban) → payment_success
//   transfer.success                          → payout_success
//   transfer.failed / transfer.reversed       → payout_failed / payout_refund
//
// Two shape differences matter and are handled here:
//   • Paystack amounts are in KOBO; NombaWebhookPayload is in NAIRA. Divided here.
//   • The receiving virtual account is at data.authorization.receiver_bank_account_number,
//     NOT data.customer — the customer object is the payer.
// ══════════════════════════════════════════════════════════════════════════════

public sealed class PaystackWebhookEndpointMarker { }

public static class PaystackWebhookEndpoint
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/webhooks/paystack",
            async (HttpRequest req, IMediator mediator, INombaClient payments, ILoggerFactory loggerFactory) =>
            {
                var logger = loggerFactory.CreateLogger<PaystackWebhookEndpointMarker>();

                req.EnableBuffering();
                using var reader = new StreamReader(req.Body, leaveOpen: true);
                var rawBody = await reader.ReadToEndAsync();
                req.Body.Position = 0;

                var signature = req.Headers["x-paystack-signature"].FirstOrDefault() ?? string.Empty;

                // Signature is computed over the RAW body — verify before parsing.
                if (!payments.VerifyWebhookSignature(rawBody, signature))
                {
                    logger.LogWarning("Paystack webhook signature verification failed.");
                    return Results.Unauthorized();
                }

                var evt = JsonSerializer.Deserialize<PaystackEvent>(rawBody, JsonOpts);
                if (evt?.Data is null)
                    return Results.BadRequest("Invalid payload.");

                var payload = MapToInternalPayload(evt, logger);

                // Unmapped events (charge.success on a card, dedicatedaccount.assign.*,
                // etc.) are acknowledged, not errored — returning non-2xx would make
                // Paystack retry something we will never act on.
                if (payload is null)
                {
                    logger.LogInformation("Paystack webhook event not handled: {Event} (channel {Channel})",
                        evt.Event, evt.Data.Channel);
                    return Results.Ok(new { processed = false, message = "Event not handled." });
                }

                var result = await mediator.Send(new ProcessWebhookCommand(payload));
                return Results.Ok(result);
            })
        .WithName("PaystackWebhook")
        .WithTags("Webhooks")
        .AllowAnonymous(); // Auth is handled via HMAC-SHA512 signature validation

    // ── Payload translation ─────────────────────────────────────────────────────

    internal static NombaWebhookPayload? MapToInternalPayload(PaystackEvent evt, ILogger logger)
    {
        var d = evt.Data!;

        return evt.Event switch
        {
            "charge.success" when IsDedicatedAccountCredit(d) => BuildPaymentPayload(d),
            "transfer.success" => BuildPayoutPayload(d, "payout_success"),
            "transfer.failed" or "transfer.reversed" => BuildPayoutPayload(
                d, evt.Event == "transfer.reversed" ? "payout_refund" : "payout_failed"),
            _ => null,
        };
    }

    // A dedicated-account credit is a bank transfer into a DVA. Card and other
    // charge.success events share the same event name, so the channel is what
    // separates "a member funded their virtual account" from everything else.
    private static bool IsDedicatedAccountCredit(PaystackEventData d) =>
        string.Equals(d.Channel, "dedicated_nuban", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(d.Authorization?.ReceiverBankAccountNumber);

    private static NombaWebhookPayload BuildPaymentPayload(PaystackEventData d) =>
        new(
            EventType: "payment_success",
            // data.reference is Paystack's unique per-transaction key and is
            // stable across webhook retries — the same role Nomba's requestId
            // plays, and what the handler's idempotency check keys on.
            RequestId: d.Reference ?? d.Id?.ToString() ?? Guid.NewGuid().ToString(),
            Data: new NombaWebhookData(
                Merchant: null,
                Terminal: null,
                Transaction: new NombaTransactionDetail(
                    TransactionId: d.Id?.ToString() ?? d.Reference ?? string.Empty,
                    Type: "payment",
                    OriginatingFrom: d.Channel,
                    Rrn: null,
                    TransactionAmount: FromKobo(d.Amount),
                    Fee: FromKobo(d.Fees),
                    Time: d.PaidAt ?? d.CreatedAt ?? DateTime.UtcNow,
                    MerchantTxRef: d.Reference,
                    AliasAccountNumber: d.Authorization!.ReceiverBankAccountNumber,
                    AliasAccountName: d.Authorization.AccountName,
                    AliasAccountType: "dedicated_nuban",
                    SessionId: null),
                Customer: new NombaCustomerDetail(
                    AccountNumber: d.Authorization.SenderBankAccountNumber,
                    BankCode: null,
                    BankName: d.Authorization.SenderBank,
                    SenderName: d.Authorization.SenderName,
                    RecipientName: d.Authorization.AccountName)));

    private static NombaWebhookPayload BuildPayoutPayload(PaystackEventData d, string eventType) =>
        new(
            EventType: eventType,
            RequestId: d.Reference ?? d.Id?.ToString() ?? Guid.NewGuid().ToString(),
            Data: new NombaWebhookData(
                Merchant: null,
                Terminal: null,
                Transaction: new NombaTransactionDetail(
                    TransactionId: d.Id?.ToString() ?? string.Empty,
                    Type: "payout",
                    OriginatingFrom: "transfer",
                    Rrn: null,
                    TransactionAmount: FromKobo(d.Amount),
                    Fee: null,
                    Time: d.UpdatedAt ?? d.CreatedAt ?? DateTime.UtcNow,
                    // HandlePayoutEventAsync matches Payout.NombaTransferRef on
                    // this value — it is the reference we sent on POST /transfer.
                    MerchantTxRef: d.Reference,
                    AliasAccountNumber: null,
                    AliasAccountName: null,
                    AliasAccountType: null,
                    SessionId: null),
                Customer: null));

    // Kobo → naira. Decimal division, so no precision is lost on odd kobo values.
    private static decimal FromKobo(long? kobo) => (kobo ?? 0) / 100m;

    // ── Paystack payload DTOs ───────────────────────────────────────────────────

    public record PaystackEvent(
        [property: JsonPropertyName("event")] string? Event,
        [property: JsonPropertyName("data")] PaystackEventData? Data);

    public record PaystackEventData(
        [property: JsonPropertyName("id")] long? Id,
        [property: JsonPropertyName("reference")] string? Reference,
        [property: JsonPropertyName("amount")] long? Amount,   // KOBO
        [property: JsonPropertyName("fees")] long? Fees,       // KOBO
        [property: JsonPropertyName("channel")] string? Channel,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("paid_at")] DateTime? PaidAt,
        [property: JsonPropertyName("createdAt")] DateTime? CreatedAt,
        [property: JsonPropertyName("updatedAt")] DateTime? UpdatedAt,
        [property: JsonPropertyName("authorization")] PaystackAuthorization? Authorization);

    public record PaystackAuthorization(
        // The DVA that RECEIVED the money — the reconciliation key.
        [property: JsonPropertyName("receiver_bank_account_number")] string? ReceiverBankAccountNumber,
        [property: JsonPropertyName("receiver_bank")] string? ReceiverBank,
        [property: JsonPropertyName("account_name")] string? AccountName,
        [property: JsonPropertyName("sender_bank")] string? SenderBank,
        [property: JsonPropertyName("sender_bank_account_number")] string? SenderBankAccountNumber,
        [property: JsonPropertyName("sender_name")] string? SenderName,
        [property: JsonPropertyName("narration")] string? Narration);
}
