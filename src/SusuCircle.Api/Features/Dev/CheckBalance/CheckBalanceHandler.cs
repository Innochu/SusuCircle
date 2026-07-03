using MediatR;
using Microsoft.Extensions.Options;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Nomba;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SusuCircle.Api.Features.Dev.CheckBalance;

// ══════════════════════════════════════════════════════════════════════════════
// DIAGNOSTIC ONLY. Checks the real Nomba parent account balance directly —
// GET /v1/accounts/balance. This is independent of webhooks entirely: it tells
// you whether money you sent has actually registered on Nomba's side at all,
// regardless of whether any webhook was ever delivered.
//
// WHY THIS MATTERS RIGHT NOW: two ₦100 transfers were confirmed to leave the
// sender's bank account, on an awake server, with no webhook received either
// time. This checks whether the money even arrived on Nomba's side — if the
// balance hasn't moved, the problem is upstream of webhooks entirely (money
// still settling, or landed somewhere other than expected). If the balance HAS
// moved, the money arrived fine and the problem is specifically webhook
// delivery for this transaction type.
// ══════════════════════════════════════════════════════════════════════════════

public record CheckBalanceQuery : IRequest<BalanceResult>;

public record BalanceResult(decimal Amount, string Currency, DateTime? TimeCreated);

public class CheckBalanceHandler(
    HttpClient http,
    INombaTokenProvider tokenProvider,
    IOptions<NombaOptions> nombaOptions,
    ILogger<CheckBalanceHandler> logger)
    : IRequestHandler<CheckBalanceQuery, BalanceResult>
{
    public async Task<BalanceResult> Handle(CheckBalanceQuery q, CancellationToken ct)
    {
        var opt = nombaOptions.Value;
        var token = await tokenProvider.GetAccessTokenAsync(ct);

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{opt.BaseUrl.TrimEnd('/')}/v1/accounts/balance");
        req.Headers.Add("accountId", opt.ParentAccountId);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            logger.LogError("Balance check failed {Status}: {Body}", resp.StatusCode, raw);
            throw new NombaApiException($"Balance check failed ({(int)resp.StatusCode}): {raw}");
        }

        var envelope = JsonSerializer.Deserialize<NombaEnvelope<BalanceData>>(raw,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var data = envelope?.Data ?? throw new NombaApiException("Balance response had no data.");

        logger.LogWarning("NOMBA BALANCE CHECK: {Amount} {Currency} as of {Time}",
            data.Amount, data.Currency, data.TimeCreated);

        return new BalanceResult(
            decimal.TryParse(data.Amount, out var amt) ? amt : 0,
            data.Currency ?? "NGN",
            data.TimeCreated);
    }

    private record BalanceData(
        [property: JsonPropertyName("amount")] string? Amount,
        [property: JsonPropertyName("currency")] string? Currency,
        [property: JsonPropertyName("timeCreated")] DateTime? TimeCreated);
}

public static class CheckBalanceEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/dev/nomba-balance",
            async (IMediator mediator) =>
            {
                var result = await mediator.Send(new CheckBalanceQuery());
                return Results.Ok(ApiResponse<BalanceResult>.Ok(result));
            })
        .WithName("CheckNombaBalance")
        .WithTags("Dev Tools")
        .AllowAnonymous();
}