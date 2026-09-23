using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Nomba;

namespace SusuCircle.Api.Common.Paystack;

// ══════════════════════════════════════════════════════════════════════════════
// Paystack implementation of INombaClient.
//
// It deliberately implements the EXISTING INombaClient interface rather than
// introducing a parallel one. Every call site — AddMemberHandler,
// TriggerPayoutHandler, SetPayoutAccountHandler, the reconciliation sweep —
// keeps working untouched; swapping providers is a single DI registration.
// The interface name is now a misnomer, but renaming it would touch a dozen
// files for no behavioural gain, so it stays until there's a reason to churn.
//
// Three things differ materially from Nomba and are handled here, not by callers:
//
//   1. AMOUNTS ARE IN KOBO. Nomba speaks plain naira; Paystack speaks kobo
//      everywhere. All conversion is confined to this file — the domain model
//      stays in naira throughout.
//
//   2. A VIRTUAL ACCOUNT NEEDS A CUSTOMER FIRST. Nomba creates a VA in one
//      call. Paystack requires POST /customer, then POST /dedicated_account
//      with the returned customer_code. CreateVirtualAccountAsync does both.
//
//   3. A TRANSFER NEEDS A RECIPIENT FIRST. Nomba transfers straight to a
//      NUBAN. Paystack requires POST /transferrecipient, then POST /transfer
//      against the returned recipient_code. InitiateTransferAsync does both.
//
// Envelope shape is uniform: { status: bool, message: string, data: {...} }.
// ══════════════════════════════════════════════════════════════════════════════

public class PaystackClient(
    HttpClient http,
    IOptions<PaystackOptions> options,
    ILogger<PaystackClient> logger) : INombaClient
{
    private readonly PaystackOptions _opt = options.Value;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── Create virtual account ──────────────────────────────────────────────────
    // Two calls: POST /customer → customer_code, then POST /dedicated_account.
    public async Task<VirtualAccountResponse> CreateVirtualAccountAsync(
        CreateVirtualAccountRequest request, CancellationToken ct = default)
    {
        var customerCode = await CreateCustomerAsync(request, ct);

        var data = await SendAsync<DedicatedAccountData>(HttpMethod.Post, "/dedicated_account", new
        {
            customer = customerCode,
            preferred_bank = _opt.ResolvedPreferredBank,
        }, ct);

        if (string.IsNullOrWhiteSpace(data.AccountNumber))
            throw new NombaApiException("Dedicated account created but no account number returned.", 502);

        var response = new VirtualAccountResponse(
            // Paystack's own handle for the DVA. Falls back to our member id so
            // this is never empty — the column is non-nullable downstream.
            AccountId: data.Id?.ToString() ?? request.AccountReference,
            AccountNumber: data.AccountNumber!,
            AccountName: data.AccountName ?? request.AccountName,
            BankName: data.Bank?.Name ?? "Paystack-Titan",
            // Paystack returns only its own internal bank id for a DVA, not an
            // NGN sort code. Storing that id would be actively misleading if
            // anything ever treated the column as a real bank code, so the slug
            // ("test-bank", "wema-bank") is stored instead — it is at least a
            // truthful identifier, and this column is only ever displayed
            // alongside the account number in payment instructions.
            BankCode: data.Bank?.Slug ?? string.Empty);

        logger.LogInformation("Paystack DVA created: {AccountNumber} ({Bank}) for {Ref}",
            response.AccountNumber, response.BankName, request.AccountReference);

        return response;
    }

    // Paystack requires an email to create a customer, but Member.Email is
    // optional in this app. Rather than reject members who were added without
    // one, synthesise a unique non-routable address from the member id. It is
    // never emailed — member email goes through INotificationService, which
    // already skips members with no address.
    private async Task<string> CreateCustomerAsync(CreateVirtualAccountRequest request, CancellationToken ct)
    {
        var email = !string.IsNullOrWhiteSpace(request.CustomerEmail)
            ? request.CustomerEmail!
            : $"member-{request.AccountReference}@susucircle.invalid";

        var (firstName, lastName) = SplitName(request.AccountName);

        var data = await SendAsync<CustomerData>(HttpMethod.Post, "/customer", new
        {
            email,
            first_name = firstName,
            last_name = lastName,
            phone = NormalizePhone(request.CustomerPhone),
        }, ct);

        if (string.IsNullOrWhiteSpace(data.CustomerCode))
            throw new NombaApiException("Paystack customer created but no customer_code returned.", 502);

        return data.CustomerCode!;
    }

    // ── Initiate bank transfer (payouts) ────────────────────────────────────────
    // Two calls: POST /transferrecipient → recipient_code, then POST /transfer.
    public async Task<TransferResponse> InitiateTransferAsync(
        InitiateTransferRequest request, CancellationToken ct = default)
    {
        var accountName = await TryLookupAccountNameAsync(request.AccountNumber, request.BankCode, ct);

        var recipient = await SendAsync<RecipientData>(HttpMethod.Post, "/transferrecipient", new
        {
            type = "nuban",
            name = accountName,
            account_number = request.AccountNumber,
            bank_code = request.BankCode,
            currency = "NGN",
        }, ct);

        if (string.IsNullOrWhiteSpace(recipient.RecipientCode))
            throw new NombaApiException("Transfer recipient created but no recipient_code returned.", 502);

        var data = await SendAsync<TransferData>(HttpMethod.Post, "/transfer", new
        {
            source = "balance",
            amount = ToKobo(request.Amount),
            recipient = recipient.RecipientCode,
            reason = request.Narration,
            // Our own idempotency key. Paystack echoes it back on transfer.*
            // webhooks, which is how HandlePayoutEventAsync finds the payout row.
            reference = request.Reference,
        }, ct);

        var response = new TransferResponse(
            TransferReference: data.Reference ?? request.Reference,
            Status: MapTransferStatus(data.Status));

        logger.LogInformation("Paystack transfer initiated: {Ref} Status: {Status}",
            response.TransferReference, response.Status);

        return response;
    }

    // Paystack statuses are lowercase and finer-grained than the app's model.
    // Map them onto the vocabulary the payout handler and webhook already use.
    private static string MapTransferStatus(string? paystackStatus) => paystackStatus?.ToLowerInvariant() switch
    {
        "success" => "SUCCESS",
        "failed" or "abandoned" => "FAILED",
        "reversed" => "REFUND",
        // "pending", "otp", "queued", "processing", null → still in flight. The
        // transfer.* webhook settles it; treating these as anything but pending
        // would mark payouts complete before the money has actually moved.
        _ => "PENDING",
    };

    // ── Webhook signature verification (HMAC-SHA512) ────────────────────────────
    // NOTE: Paystack signs with SHA-512 over the RAW request body, keyed by the
    // SECRET KEY itself — not SHA-256, and not a separate webhook secret as
    // Nomba uses. Signature is lowercase hex in the x-paystack-signature header.
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        if (string.IsNullOrWhiteSpace(_opt.SecretKey))
            throw new InvalidOperationException("Paystack secret key not configured");
        if (string.IsNullOrWhiteSpace(signature))
            return false;

        var keyBytes = Encoding.UTF8.GetBytes(_opt.SecretKey);
        using var hmac = new HMACSHA512(keyBytes);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var expected = Convert.ToHexString(hash).ToLowerInvariant();

        return FixedTimeEquals(signature.Trim().ToLowerInvariant(), expected);
    }

    // ── Bank account lookup ─────────────────────────────────────────────────────
    // THROWS on failure — used by member self-service payout setup, where an
    // unresolvable account must be rejected, never saved on optimism.
    public async Task<BankLookupResult> LookupBankAccountAsync(
        string accountNumber, string bankCode, CancellationToken ct = default)
    {
        var path = $"/bank/resolve?account_number={Uri.EscapeDataString(accountNumber)}" +
                   $"&bank_code={Uri.EscapeDataString(bankCode)}";

        var data = await SendAsync<ResolveData>(HttpMethod.Get, path, body: null, ct);

        if (string.IsNullOrWhiteSpace(data.AccountName))
            throw new NombaApiException("Could not resolve an account name for that account number/bank.");

        return new BankLookupResult(data.AccountNumber ?? accountNumber, data.AccountName!);
    }

    // Best-effort variant used before a transfer. Deliberately tolerant: a payout
    // shouldn't be blocked because the name-verification call had a hiccup. This
    // mirrors NombaClient's split of the same concern.
    private async Task<string> TryLookupAccountNameAsync(string accountNumber, string bankCode, CancellationToken ct)
    {
        try
        {
            var result = await LookupBankAccountAsync(accountNumber, bankCode, ct);
            return result.AccountName;
        }
        catch (NombaApiException ex)
        {
            logger.LogWarning(ex, "Account lookup failed for {Acct}; proceeding without verified name.", accountNumber);
            return "Susu Circle Member";
        }
    }

    // ── Internal HTTP plumbing ───────────────────────────────────────────────────
    // Attaches the bearer secret key, then unwraps { status, message, data }.
    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var missing = _opt.MissingSettings();
        if (missing.Count > 0)
        {
            var keys = string.Join(", ", missing);
            logger.LogError("Paystack is not configured. Missing or placeholder settings: {Missing}", keys);
            throw new NombaApiException(
                $"Paystack is not configured — set {keys} (user-secrets, environment variables, or appsettings).");
        }

        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.SecretKey);

        if (body is not null)
            req.Content = JsonContent.Create(body, options: JsonOpts);

        using var resp = await http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            logger.LogError("Paystack API error {Status} on {Method} {Path}: {Body}",
                resp.StatusCode, method, path, raw);

            // Surface Paystack's own message — it is specific and actionable
            // ("Customer not found", "Your business is not activated for
            // dedicated accounts"), where a bare status code says nothing.
            var detail = ExtractMessage(raw) ?? (raw.Length > 500 ? raw[..500] + "…" : raw);
            throw new NombaApiException($"Paystack API returned {(int)resp.StatusCode}: {detail}", (int)resp.StatusCode);
        }

        var envelope = JsonSerializer.Deserialize<PaystackEnvelope<T>>(raw, JsonOpts);

        // Paystack can return HTTP 200 with status:false — treat that as an error.
        if (envelope is null || !envelope.Status)
        {
            var msg = envelope?.Message ?? "Paystack returned a non-success response.";
            throw new NombaApiException(msg, (int)resp.StatusCode);
        }

        return envelope.Data
            ?? throw new NombaApiException("Paystack API returned empty data.", (int)resp.StatusCode);
    }

    private static string? ExtractMessage(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    // Naira → kobo. Rounds to the nearest kobo before casting so that a value
    // like 29999.999 from a decimal division can't silently truncate a kobo.
    internal static long ToKobo(decimal naira) => (long)Math.Round(naira * 100m, MidpointRounding.AwayFromZero);

    // Paystack wants a local 11-digit Nigerian number or +234 form; normalise the
    // common 0-prefixed input to +234 so both render consistently on their side.
    private static string NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return string.Empty;

        var digits = new string(phone.Where(char.IsDigit).ToArray());

        if (digits.StartsWith("234")) return "+" + digits;
        if (digits.StartsWith('0') && digits.Length == 11) return "+234" + digits[1..];
        return phone.Trim();
    }

    // "Ngozi Obi" → ("Ngozi", "Obi"); a single word becomes the first name with
    // the account name repeated as surname, since Paystack rejects an empty one.
    private static (string First, string Last) SplitName(string fullName)
    {
        var parts = fullName?.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];

        return parts.Length switch
        {
            0 => ("Susu", "Member"),
            1 => (parts[0], parts[0]),
            _ => (parts[0], string.Join(' ', parts[1..])),
        };
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    // ── Response DTOs ───────────────────────────────────────────────────────────

    private record PaystackEnvelope<T>(
        [property: JsonPropertyName("status")] bool Status,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("data")] T? Data);

    private record CustomerData(
        [property: JsonPropertyName("customer_code")] string? CustomerCode,
        [property: JsonPropertyName("id")] long? Id);

    private record DedicatedAccountData(
        [property: JsonPropertyName("id")] long? Id,
        [property: JsonPropertyName("account_number")] string? AccountNumber,
        [property: JsonPropertyName("account_name")] string? AccountName,
        [property: JsonPropertyName("bank")] DedicatedBank? Bank);

    private record DedicatedBank(
        [property: JsonPropertyName("id")] long? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("slug")] string? Slug);

    private record RecipientData(
        [property: JsonPropertyName("recipient_code")] string? RecipientCode);

    private record TransferData(
        [property: JsonPropertyName("transfer_code")] string? TransferCode,
        [property: JsonPropertyName("reference")] string? Reference,
        [property: JsonPropertyName("status")] string? Status);

    private record ResolveData(
        [property: JsonPropertyName("account_number")] string? AccountNumber,
        [property: JsonPropertyName("account_name")] string? AccountName);
}
