using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SusuCircle.Api.Common.Exceptions;

namespace SusuCircle.Api.Common.Nomba;

// ── DTOs (unchanged from the original) ──────────────────────────────────────────
// NOTE: NombaWebhookPayload is NOT defined here anymore — it now lives in its own
// file, NombaWebhookPayload.cs, as a nested record matching the real API shape.
// If you have an OLD standalone NombaContracts.cs from an earlier round, DELETE
// IT — it duplicates these same DTOs and defines its own conflicting
// NombaApiException, which is what caused the "cannot convert int to string?"
// errors.

public record CreateVirtualAccountRequest(
    string AccountName,
    string AccountReference,
    string CustomerPhone,
    string? CustomerEmail = null);

public record VirtualAccountResponse(
    string AccountId,
    string AccountNumber,
    string AccountName,
    string BankName,
    string BankCode);

public record InitiateTransferRequest(
    string AccountNumber,
    string BankCode,
    decimal Amount,
    string Narration,
    string Reference);

public record TransferResponse(
    string TransferReference,
    string Status);

// ── Client ────────────────────────────────────────────────────────────────────

public interface INombaClient
{
    Task<VirtualAccountResponse> CreateVirtualAccountAsync(CreateVirtualAccountRequest request, CancellationToken ct = default);
    Task<TransferResponse> InitiateTransferAsync(InitiateTransferRequest request, CancellationToken ct = default);
    bool VerifyWebhookSignature(string payload, string signature);
}

public class NombaClient(
    HttpClient http,
    INombaTokenProvider tokenProvider,
    IOptions<NombaOptions> options,
    ILogger<NombaClient> logger) : INombaClient
{
    private readonly NombaOptions _opt = options.Value;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // ── Create virtual account ──────────────────────────────────────────────────
    // POST /v1/accounts/virtual → { code, description, data: { accountRef, bankAccountNumber, bankName, accountName, bankCode } }
    public async Task<VirtualAccountResponse> CreateVirtualAccountAsync(
        CreateVirtualAccountRequest request, CancellationToken ct = default)
    {
        var body = new
        {
            accountRef = request.AccountReference,
            accountName = request.AccountName,
            phoneNumber = NormalizePhone(request.CustomerPhone),
            email = request.CustomerEmail,
            currency = "NGN",
        };

        var data = await SendAsync<VaData>(HttpMethod.Post, "/v1/accounts/virtual", body, ct);

        if (string.IsNullOrWhiteSpace(data.BankAccountNumber))
            throw new NombaApiException("Virtual account created but no account number returned.", 502);

        var response = new VirtualAccountResponse(
            AccountId: data.AccountRef ?? request.AccountReference,
            AccountNumber: data.BankAccountNumber!,
            AccountName: data.AccountName ?? request.AccountName,
            BankName: data.BankName ?? "Nomba MFB",
            BankCode: data.BankCode ?? _opt.DefaultBankCode);

        logger.LogInformation("VA created: {AccountNumber}", response.AccountNumber);
        return response;
    }

    // ── Initiate bank transfer (payouts) ────────────────────────────────────────
    // POST /v2/transfers/bank (or /v2/transfers/bank/{subAccountId} if scoped)
    // → { code, description, data: { id, status } }
    public async Task<TransferResponse> InitiateTransferAsync(
        InitiateTransferRequest request, CancellationToken ct = default)
    {
        var path = string.IsNullOrWhiteSpace(_opt.SubAccountId)
            ? "/v2/transfers/bank"
            : $"/v2/transfers/bank/{_opt.SubAccountId}";

        // Nomba recommends verifying the recipient before transferring.
        var accountName = await TryLookupAccountNameAsync(request.AccountNumber, request.BankCode, ct);

        var body = new
        {
            amount = request.Amount,
            accountNumber = request.AccountNumber,
            accountName,
            bankCode = request.BankCode,
            merchantTxRef = request.Reference, // idempotency key — unique per transfer
            senderName = "Susu Circle",
            narration = request.Narration,
        };

        var data = await SendAsync<TransferData>(HttpMethod.Post, path, body, ct);

        var response = new TransferResponse(
            TransferReference: data.Id ?? request.Reference,
            Status: data.Status ?? "PENDING");

        logger.LogInformation("Transfer initiated: {Ref} Status: {Status}", response.TransferReference, response.Status);
        return response;
    }

    // ── Webhook signature verification (HMAC-SHA256) ────────────────────────────
    // Reads the secret from config key "Nomba:WebhookSecret" via NombaOptions.
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        if (string.IsNullOrWhiteSpace(_opt.WebhookSecret))
            throw new InvalidOperationException("Nomba webhook secret not configured");
        if (string.IsNullOrWhiteSpace(signature))
            return false;

        var keyBytes = Encoding.UTF8.GetBytes(_opt.WebhookSecret);
        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));

        var expectedHex = Convert.ToHexString(hash).ToLowerInvariant();
        var expectedB64 = Convert.ToBase64String(hash);
        var provided = signature.Trim();

        // Accept hex (original behaviour) or base64, in constant time.
        return FixedTimeEquals(provided.ToLowerInvariant(), expectedHex)
            || FixedTimeEquals(provided, expectedB64);
    }

    // ── Bank account lookup (best-effort, used before transfers) ────────────────
    private async Task<string> TryLookupAccountNameAsync(string accountNumber, string bankCode, CancellationToken ct)
    {
        try
        {
            var body = new { accountNumber, bankCode };
            var data = await SendAsync<LookupData>(HttpMethod.Post, "/v1/transfers/bank/lookup", body, ct);
            return data.AccountName ?? "Susu Circle Member";
        }
        catch (NombaApiException ex)
        {
            logger.LogWarning(ex, "Account lookup failed for {Acct}; proceeding without verified name.", accountNumber);
            return "Susu Circle Member";
        }
    }

    // ── Internal HTTP plumbing ───────────────────────────────────────────────────
    // Attaches the accountId header + bearer token, then unwraps { code, description, data }.
    private async Task<T> SendAsync<T>(HttpMethod method, string path, object body, CancellationToken ct)
    {
        var token = await tokenProvider.GetAccessTokenAsync(ct);

        var json = JsonSerializer.Serialize(body, JsonOpts);
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("accountId", _opt.ParentAccountId);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            logger.LogError("Nomba API error {Status}: {Body}", resp.StatusCode, raw);
            throw new NombaApiException($"Nomba API returned {resp.StatusCode}: {raw}", (int)resp.StatusCode);
        }

        var envelope = JsonSerializer.Deserialize<NombaEnvelope<T>>(raw, JsonOpts);

        // Nomba's success code is "00" — treat anything else as an error even on HTTP 200.
        if (envelope is null || (envelope.Code is not null && envelope.Code != "00"))
        {
            var msg = envelope?.Description ?? "Nomba returned a non-success code.";
            throw new NombaApiException(msg, (int)resp.StatusCode);
        }

        return envelope.Data
            ?? throw new NombaApiException("Nomba API returned empty response.", (int)resp.StatusCode);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    // Nomba expects intl format e.g. 2348012345678; convert 0801... → 234801...
    private static string NormalizePhone(string phone)
    {
        phone = phone.Trim();
        if (phone.StartsWith('0') && phone.Length == 11) return "234" + phone[1..];
        return phone;
    }

    // ── Raw response data shapes (subset of Nomba's actual fields) ──
    private record VaData(
        [property: JsonPropertyName("accountRef")] string? AccountRef,
        [property: JsonPropertyName("bankAccountNumber")] string? BankAccountNumber,
        [property: JsonPropertyName("bankName")] string? BankName,
        [property: JsonPropertyName("accountName")] string? AccountName,
        [property: JsonPropertyName("bankCode")] string? BankCode);

    private record LookupData(
        [property: JsonPropertyName("accountNumber")] string? AccountNumber,
        [property: JsonPropertyName("accountName")] string? AccountName);

    private record TransferData(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("status")] string? Status);
}