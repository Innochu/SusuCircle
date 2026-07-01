//namespace SusuCircle.Api.Common.Nomba;

//// ══════════════════════════════════════════════════════════════════════════════
//// Contracts for the Nomba API. Field names mirror the real API (docs verified).
//// The client maps between these and the JSON envelopes { code, description, data }.
//// ══════════════════════════════════════════════════════════════════════════════

//// ── Virtual Account creation ──
//// POST /v1/accounts/virtual
////   body: { accountRef, accountName, ... }
////   data: { accountRef, accountName, bankName, bankAccountNumber, ... }
//public record CreateVirtualAccountRequest(
//    string AccountName,
//    string AccountReference,   // our Member.Id — stable, idempotent per member
//    string? CustomerPhone,
//    string? CustomerEmail);

//public record VirtualAccountResponse(
//    string AccountId,          // maps from data.accountRef (Nomba's handle)
//    string AccountNumber,      // maps from data.bankAccountNumber (the 10-digit NUBAN)
//    string BankName);          // maps from data.bankName

//// ── Bank transfer (payouts) ──
//// POST /v2/transfers/bank            (from parent)
//// POST /v2/transfers/bank/{subId}    (from sub-account, must be enabled by Nomba)
////   body: { amount, accountNumber, accountName, bankCode, merchantTxRef, senderName, narration }
////   data: { id, status }
//public record InitiateTransferRequest(
//    string AccountNumber,      // destination NUBAN
//    string BankCode,
//    decimal Amount,
//    string Narration,
//    string Reference,          // merchantTxRef — idempotency key, unique per transfer
//    string? AccountName = null); // optional; client resolves via lookup when null

//public record TransferResponse(
//    string TransferReference,  // maps from data.id
//    string Status);            // SUCCESS | PENDING | FAILED | REFUND

//// ── Bank account lookup (verify recipient before transfer) ──
//// POST /v1/transfers/bank/lookup   body: { accountNumber, bankCode }
//public record BankLookupRequest(string AccountNumber, string BankCode);
//public record BankLookupResponse(string AccountNumber, string AccountName);

//// ── Errors ──
//public class NombaApiException : Exception
//{
//    public string? Code { get; }
//    public NombaApiException(string message, string? code = null) : base(message) => Code = code;
//    public NombaApiException(string message, Exception inner) : base(message, inner) { }
//}