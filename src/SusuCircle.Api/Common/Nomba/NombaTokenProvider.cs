using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SusuCircle.Api.Common.Exceptions;

namespace SusuCircle.Api.Common.Nomba;

// Obtains and caches the Nomba access_token (client_credentials grant).
// Tokens expire after ~30 min; cached and refreshed ~5 min early.
// Single-flight lock so concurrent requests don't each hit the token endpoint.
public interface INombaTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken ct = default);
}

public class NombaTokenProvider(
    HttpClient http,
    IOptions<NombaOptions> options,
    ILogger<NombaTokenProvider> logger) : INombaTokenProvider
{
    private readonly NombaOptions _opt = options.Value;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string? _accessToken;
    private DateTime _expiresAtUtc = DateTime.MinValue;

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_accessToken is not null && DateTime.UtcNow < _expiresAtUtc.AddMinutes(-5))
            return _accessToken;

        await _lock.WaitAsync(ct);
        try
        {
            if (_accessToken is not null && DateTime.UtcNow < _expiresAtUtc.AddMinutes(-5))
                return _accessToken;

            // Fail with the specific config key that's missing. Without this the
            // request goes out with an empty client_secret and Nomba answers with
            // a generic 401, which surfaces to the caller as "virtual account
            // provisioning failed" and says nothing about the real cause.
            var missing = _opt.MissingSettings();
            if (missing.Count > 0)
            {
                var keys = string.Join(", ", missing);
                logger.LogError("Nomba is not configured. Missing or placeholder settings: {Missing}", keys);
                throw new NombaApiException(
                    $"Nomba is not configured — set {keys} (user-secrets, environment variables, or appsettings).");
            }

            using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/token/issue");
            req.Headers.Add("accountId", _opt.ResolvedAccountId);
            req.Content = JsonContent.Create(new
            {
                grant_type = "client_credentials",
                client_id = _opt.ResolvedClientId,
                client_secret = _opt.ResolvedClientSecret,
            });

            using var res = await http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
            {
                logger.LogError("Nomba token issue POST {Url} failed: {Status} {Body}",
                    new Uri(http.BaseAddress!, "/v1/auth/token/issue"), res.StatusCode, body);

                // Include Nomba's own response body — a bare status code says
                // nothing about WHY. A 404 here usually means the credentials
                // belong to the other environment (live keys against
                // sandbox.nomba.com, or sandbox keys against api.nomba.com), so
                // the parent account simply is not found on this host.
                var detail = string.IsNullOrWhiteSpace(body)
                    ? "(empty response body)"
                    : body.Length > 500 ? body[..500] + "…" : body;

                throw new NombaApiException(
                    $"Token issuance failed ({(int)res.StatusCode}) at {http.BaseAddress}v1/auth/token/issue: {detail}",
                    (int)res.StatusCode);
            }

            var parsed = JsonSerializer.Deserialize<NombaEnvelope<TokenData>>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var data = parsed?.Data
                ?? throw new NombaApiException("Token response had no data.");

            _accessToken = data.AccessToken;
            _expiresAtUtc = data.ExpiresAt ?? DateTime.UtcNow.AddMinutes(30);

            logger.LogInformation("Nomba access token obtained, expires {Expiry:u}", _expiresAtUtc);
            return _accessToken!;
        }
        finally
        {
            _lock.Release();
        }
    }

    private record TokenData(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expiresAt")] DateTime? ExpiresAt);
}

// Generic envelope wrapper Nomba uses: { code, description, data }
public record NombaEnvelope<T>(
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("data")] T? Data);