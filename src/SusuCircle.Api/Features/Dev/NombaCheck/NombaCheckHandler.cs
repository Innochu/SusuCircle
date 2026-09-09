using System.Net.Http.Json;
using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Options;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Nomba;

namespace SusuCircle.Api.Features.Dev.NombaCheck;

// ══════════════════════════════════════════════════════════════════════════════
// DIAGNOSTIC ONLY. Answers "why isn't a virtual account being generated?"
// without having to add a member and read through the logs:
//
//   GET  /api/dev/nomba-check      → config keys set? token issued? which host?
//   POST /api/dev/nomba-check/va   → actually provision a throwaway VA
//
// Reports only WHETHER each secret is configured, never its value.
//
// HOST PROBE: when the configured host rejects the credentials, this also tries
// the OTHER Nomba host (sandbox vs live) with the same credentials. Nomba issues
// separate credential pairs per environment, and a parent account that does not
// exist on a host answers 404 — so "404 here, 200 there" identifies the mix-up
// immediately. Both hosts are Nomba's own token endpoint and issuing a token has
// no side effects.
// ══════════════════════════════════════════════════════════════════════════════

public record NombaCheckQuery : IRequest<NombaCheckResult>;

public record HostProbe(string Host, int? HttpStatus, bool TokenIssued, string? Detail);

public record NombaCheckResult(
    string BaseUrl,
    bool Configured,
    IReadOnlyList<string> MissingSettings,
    IReadOnlyDictionary<string, bool> SettingsPresent,
    bool TokenAcquired,
    string? TokenError,
    IReadOnlyList<HostProbe> HostProbes,
    string? Diagnosis);

public class NombaCheckHandler(
    HttpClient http,
    INombaTokenProvider tokenProvider,
    IOptions<NombaOptions> options,
    ILogger<NombaCheckHandler> logger)
    : IRequestHandler<NombaCheckQuery, NombaCheckResult>
{
    private const string SandboxHost = "https://sandbox.nomba.com";
    private const string LiveHost = "https://api.nomba.com";

    public async Task<NombaCheckResult> Handle(NombaCheckQuery q, CancellationToken ct)
    {
        var opt = options.Value;
        var missing = opt.MissingSettings();

        var present = new Dictionary<string, bool>
        {
            ["ClientId"] = opt.ResolvedClientId.Length > 0,
            ["ClientSecret/PrivateKey"] = opt.ResolvedClientSecret.Length > 0,
            ["ParentAccountId/AccountId"] = opt.ResolvedAccountId.Length > 0,
            ["SubAccountId"] = !string.IsNullOrWhiteSpace(opt.SubAccountId),
            ["WebhookSecret"] = !string.IsNullOrWhiteSpace(opt.WebhookSecret),
        };

        var probes = new List<HostProbe>();

        // Open-sandbox mode needs no token at all — report that plainly rather
        // than chasing a credential failure that no longer matters.
        if (opt.UnauthenticatedSandboxActive)
        {
            return new NombaCheckResult(opt.BaseUrl, true, Array.Empty<string>(), present, false, null, probes,
                "Unauthenticated sandbox mode is ON. Calls go to sandbox.nomba.com with no credentials, " +
                "so no access token is needed and virtual accounts should generate. Turn " +
                "Nomba:UseUnauthenticatedSandbox off once you have working credentials.");
        }

        if (missing.Count > 0)
        {
            return new NombaCheckResult(opt.BaseUrl, false, missing, present, false,
                $"Not attempted — missing {string.Join(", ", missing)}.",
                probes, "Set the missing configuration keys, then re-run this check.");
        }

        var tokenAcquired = false;
        string? tokenError = null;

        try
        {
            var token = await tokenProvider.GetAccessTokenAsync(ct);
            tokenAcquired = !string.IsNullOrWhiteSpace(token);
        }
        catch (NombaApiException ex)
        {
            tokenError = ex.Message;
        }

        if (tokenAcquired)
        {
            return new NombaCheckResult(opt.BaseUrl, true, missing, present, true, null,
                probes, "Credentials work. Virtual account creation should succeed.");
        }

        // Configured host failed — probe both hosts directly to find out where
        // these credentials actually live.
        foreach (var host in new[] { SandboxHost, LiveHost })
            probes.Add(await ProbeHostAsync(host, opt, ct));

        var working = probes.FirstOrDefault(p => p.TokenIssued);
        var configuredHost = opt.BaseUrl.TrimEnd('/');

        var diagnosis = working is not null
            ? $"These credentials are valid at {working.Host} but not at the configured {configuredHost}. " +
              $"Set Nomba:BaseUrl to {working.Host} and restart."
            : "Neither Nomba host accepted these credentials. Re-copy ClientId, PrivateKey and " +
              "ParentAccountId from the Nomba dashboard, making sure all three come from the SAME " +
              "environment tab (Sandbox or Live) — a parent accountId from the other environment returns 404.";

        return new NombaCheckResult(opt.BaseUrl, true, missing, present,
            false, tokenError, probes, diagnosis);
    }

    private async Task<HostProbe> ProbeHostAsync(string host, NombaOptions opt, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{host}/v1/auth/token/issue");
            req.Headers.Add("accountId", opt.ResolvedAccountId);
            req.Content = JsonContent.Create(new
            {
                grant_type = "client_credentials",
                client_id = opt.ResolvedClientId,
                client_secret = opt.ResolvedClientSecret,
            });

            using var res = await http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);

            var issued = false;
            if (res.IsSuccessStatusCode)
            {
                var parsed = JsonSerializer.Deserialize<NombaEnvelope<TokenPeek>>(body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                issued = !string.IsNullOrWhiteSpace(parsed?.Data?.AccessToken);
            }

            logger.LogInformation("Nomba host probe {Host} returned {Status}", host, res.StatusCode);

            // Never echo the token itself — on success just say so.
            return new HostProbe(host, (int)res.StatusCode, issued,
                issued ? "Token issued." : Truncate(body));
        }
        catch (Exception ex)
        {
            return new HostProbe(host, null, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string Truncate(string s) =>
        string.IsNullOrWhiteSpace(s)
            ? "(empty response body)"
            : s.Length > 300 ? s[..300] + "…" : s;

    private record TokenPeek(string? AccessToken);
}

// ── Live VA provisioning smoke test ───────────────────────────────────────────
// Creates a real virtual account at Nomba against a throwaway reference. Nothing
// is written to the database — this only proves the credentials and the
// /v1/accounts/virtual call work end to end.

public record NombaVaSmokeTestCommand(string? AccountName, string? Phone) : IRequest<VirtualAccountResponse>;

public class NombaVaSmokeTestHandler(INombaClient nomba)
    : IRequestHandler<NombaVaSmokeTestCommand, VirtualAccountResponse>
{
    public Task<VirtualAccountResponse> Handle(NombaVaSmokeTestCommand cmd, CancellationToken ct) =>
        nomba.CreateVirtualAccountAsync(new CreateVirtualAccountRequest(
            AccountName: string.IsNullOrWhiteSpace(cmd.AccountName) ? "Susu Smoke Test" : cmd.AccountName,
            AccountReference: $"smoketest-{Guid.NewGuid():N}",
            CustomerPhone: string.IsNullOrWhiteSpace(cmd.Phone) ? "08000000000" : cmd.Phone,
            CustomerEmail: null), ct);
}

public static class NombaCheckEndpoint
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/dev/nomba-check",
            async (IMediator mediator) =>
            {
                var result = await mediator.Send(new NombaCheckQuery());
                return Results.Ok(ApiResponse<NombaCheckResult>.Ok(result));
            })
        .WithName("NombaConfigCheck")
        .WithTags("Dev Tools")
        .AllowAnonymous();

        app.MapPost("/api/dev/nomba-check/va",
            async (NombaVaSmokeTestCommand? cmd, IMediator mediator) =>
            {
                var result = await mediator.Send(cmd ?? new NombaVaSmokeTestCommand(null, null));
                return Results.Ok(ApiResponse<VirtualAccountResponse>.Ok(result));
            })
        .WithName("NombaVirtualAccountSmokeTest")
        .WithTags("Dev Tools")
        .AllowAnonymous();
    }
}
