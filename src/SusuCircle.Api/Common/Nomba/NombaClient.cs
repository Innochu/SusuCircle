using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SusuCircle.Api.Common.Exceptions;

namespace SusuCircle.Api.Common.Nomba;

// ── DTOs ─────────────────────────────────────────────────────────────────────

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

public record NombaWebhookPayload(
    string EventType,
    string AccountNumber,
    decimal Amount,
    string TransactionReference,
    string Narration,
    DateTime Timestamp);

// ── Client ────────────────────────────────────────────────────────────────────

public interface INombaClient
{
    Task<VirtualAccountResponse> CreateVirtualAccountAsync(CreateVirtualAccountRequest request, CancellationToken ct = default);
    Task<TransferResponse> InitiateTransferAsync(InitiateTransferRequest request, CancellationToken ct = default);
    bool VerifyWebhookSignature(string payload, string signature);
}

public class NombaClient(HttpClient http, IConfiguration config, ILogger<NombaClient> logger) : INombaClient
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<VirtualAccountResponse> CreateVirtualAccountAsync(CreateVirtualAccountRequest request, CancellationToken ct = default)
    {
        var response = await PostAsync<VirtualAccountResponse>("v1/accounts/virtual", request, ct);
        logger.LogInformation("VA created: {AccountNumber}", response.AccountNumber);
        return response;
    }

    public async Task<TransferResponse> InitiateTransferAsync(InitiateTransferRequest request, CancellationToken ct = default)
    {
        var response = await PostAsync<TransferResponse>("v1/transfers", request, ct);
        logger.LogInformation("Transfer initiated: {Ref} Status: {Status}", response.TransferReference, response.Status);
        return response;
    }

    public bool VerifyWebhookSignature(string payload, string signature)
    {
        var secret  = config["Nomba:WebhookSecret"] ?? throw new InvalidOperationException("Nomba webhook secret not configured");
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        using var hmac = new System.Security.Cryptography.HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var expected = Convert.ToHexString(hash).ToLowerInvariant();
        return expected == signature.ToLowerInvariant();
    }

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        var json    = JsonSerializer.Serialize(body, JsonOpts);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var resp = await http.PostAsync(path, content, ct);
        var raw  = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            logger.LogError("Nomba API error {Status}: {Body}", resp.StatusCode, raw);
            throw new NombaApiException($"Nomba API returned {resp.StatusCode}: {raw}", (int)resp.StatusCode);
        }

        return JsonSerializer.Deserialize<T>(raw, JsonOpts)
            ?? throw new NombaApiException("Nomba API returned empty response.");
    }
}
