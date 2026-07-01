using System.Text.Json.Serialization;

namespace SusuCircle.Api.Common.Nomba;

// ══════════════════════════════════════════════════════════════════════════════
// Matches the REAL Nomba webhook payload shape, verified against
// https://developer.nomba.com/products/webhooks/introduction — not the earlier
// flat guess, and not the training module's simplified "event"/"amountReceived"
// example (those field names don't exist in the live docs).
//
// Real event types: payment_success, payout_success, payment_failed,
//                    payment_reversal, payout_failed, payout_refund
// (there is NO "virtual_account.funded" or "INWARD_TRANSFER" event — an inbound
// VA credit arrives as payment_success)
//
// Amounts are plain decimal NAIRA (confirmed: "transactionAmount": 1000 with
// "fee": 5 in the docs' own example — a 0.5% fee only makes sense as naira, not
// kobo — and the VA-creation docs use "expectedAmount": "200.00"). No kobo
// conversion needed anywhere in this integration.
// ══════════════════════════════════════════════════════════════════════════════

public record NombaWebhookPayload(
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("data")] NombaWebhookData Data);

public record NombaWebhookData(
    [property: JsonPropertyName("merchant")] NombaMerchantDetail? Merchant,
    [property: JsonPropertyName("terminal")] NombaTerminalDetail? Terminal,
    [property: JsonPropertyName("transaction")] NombaTransactionDetail Transaction,
    [property: JsonPropertyName("customer")] NombaCustomerDetail? Customer);

public record NombaMerchantDetail(
    [property: JsonPropertyName("userId")] string? UserId,
    [property: JsonPropertyName("walletId")] string? WalletId,
    [property: JsonPropertyName("walletBalance")] decimal? WalletBalance);

public record NombaTerminalDetail(
    [property: JsonPropertyName("terminalId")] string? TerminalId,
    [property: JsonPropertyName("terminalLabel")] string? TerminalLabel);

public record NombaTransactionDetail(
    [property: JsonPropertyName("transactionId")] string TransactionId,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("originatingFrom")] string? OriginatingFrom,
    [property: JsonPropertyName("rrn")] string? Rrn,
    [property: JsonPropertyName("transactionAmount")] decimal TransactionAmount, // NAIRA, not kobo
    [property: JsonPropertyName("fee")] decimal? Fee,
    [property: JsonPropertyName("time")] DateTime Time,
    [property: JsonPropertyName("merchantTxRef")] string? MerchantTxRef,
    // This is the VA number that RECEIVED the money — the reconciliation key.
    [property: JsonPropertyName("aliasAccountNumber")] string? AliasAccountNumber,
    [property: JsonPropertyName("aliasAccountName")] string? AliasAccountName,
    [property: JsonPropertyName("aliasAccountType")] string? AliasAccountType,
    [property: JsonPropertyName("sessionId")] string? SessionId);

public record NombaCustomerDetail(
    [property: JsonPropertyName("accountNumber")] string? AccountNumber,
    [property: JsonPropertyName("bankCode")] string? BankCode,
    [property: JsonPropertyName("bankName")] string? BankName,
    [property: JsonPropertyName("senderName")] string? SenderName,
    [property: JsonPropertyName("recipientName")] string? RecipientName);