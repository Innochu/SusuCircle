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

            using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/token/issue");
            req.Headers.Add("accountId", _opt.ParentAccountId);
            req.Content = JsonContent.Create(new
            {
                grant_type = "client_credentials",
                client_id = _opt.ClientId,
                client_secret = _opt.ClientSecret,
            });

            using var res = await http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
            {
                logger.LogError("Nomba token issue failed: {Status} {Body}", res.StatusCode, body);
                throw new NombaApiException($"Token issuance failed ({(int)res.StatusCode}).", (int)res.StatusCode);
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