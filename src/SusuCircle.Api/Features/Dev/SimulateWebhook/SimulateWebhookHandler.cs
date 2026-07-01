using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Nomba;
using SusuCircle.Api.Common.Persistence;
using SusuCircle.Api.Features.Webhooks.NombaWebhook;

namespace SusuCircle.Api.Features.Dev.SimulateWebhook;

public record SimulateWebhookCommand(Guid MemberId, decimal Amount) : IRequest<WebhookResult>;

public class SimulateWebhookHandler(AppDbContext db, IMediator mediator, ILogger<SimulateWebhookHandler> logger)
    : IRequestHandler<SimulateWebhookCommand, WebhookResult>
{
    public async Task<WebhookResult> Handle(SimulateWebhookCommand cmd, CancellationToken ct)
    {
        var member = await db.Members
            .FirstOrDefaultAsync(m => m.Id == cmd.MemberId, ct)
            ?? throw new NotFoundException(nameof(Member), cmd.MemberId);

        if (string.IsNullOrWhiteSpace(member.VirtualAccountNumber))
            throw new ConflictException("Member has no virtual account provisioned.");

        var requestId = "SIMWEBHOOK-" + Guid.NewGuid().ToString("N");
        var transactionId = "SIM-TXN-" + Guid.NewGuid().ToString("N");

        var payload = new NombaWebhookPayload(
            EventType: "payment_success",
            RequestId: requestId,
            Data: new NombaWebhookData(
                Merchant: null,
                Terminal: null,
                Transaction: new NombaTransactionDetail(
                    TransactionId: transactionId,
                    Type: "virtual_account_credit",
                    OriginatingFrom: "simulated",
                    Rrn: null,
                    TransactionAmount: cmd.Amount,
                    Fee: 0,
                    Time: DateTime.UtcNow,
                    MerchantTxRef: null,
                    AliasAccountNumber: member.VirtualAccountNumber,
                    AliasAccountName: member.Name,
                    AliasAccountType: "virtual",
                    SessionId: null),
                Customer: new NombaCustomerDetail(
                    AccountNumber: "0000000000",
                    BankCode: null,
                    BankName: "Simulated Sender Bank",
                    SenderName: "Simulated Test Sender",
                    RecipientName: member.Name)));

        logger.LogWarning(
            "SIMULATED WEBHOOK bypassing Nomba delivery. Member {MemberName} ({Va}) amount NGN{Amount} requestId {RequestId}",
            member.Name, member.VirtualAccountNumber, cmd.Amount, requestId);

        var result = await mediator.Send(new ProcessWebhookCommand(payload), ct);

        logger.LogWarning("SIMULATED WEBHOOK result: {Processed} - {Message}", result.Processed, result.Message);

        return result;
    }
}

public record SimulateWebhookRequest(Guid MemberId, decimal Amount);

public static class SimulateWebhookEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/dev/simulate-webhook",
            async (SimulateWebhookRequest body, IMediator mediator) =>
            {
                var result = await mediator.Send(new SimulateWebhookCommand(body.MemberId, body.Amount));
                return Results.Ok(ApiResponse<WebhookResult>.Ok(
                    result, "Simulated webhook processed directly (Nomba delivery bypassed)."));
            })
        .WithName("SimulateWebhook")
        .WithTags("Dev Tools")
        .AllowAnonymous();
}