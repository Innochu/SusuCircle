using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Nomba;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Features.Dev.SimulateTransfer;

// ══════════════════════════════════════════════════════════════════════════════
// DEV/TEST TOOL ONLY. Simulates "a member paying their contribution" by calling
// Nomba's OWN outbound transfer API (/v2/transfers/bank) with the member's
// virtual account number as the destination. This is exactly how Nomba's docs
// say to test inbound funding in sandbox — there's no separate "fund VA"
// endpoint; you use the transfer API as if you were the paying customer.
//
// This does NOT touch the database directly — it only calls Nomba. Reconciliation
// still happens for real, through your actual webhook (NombaWebhookHandler),
// exactly as it would for a genuine payment. That's the point: this tests the
// real end-to-end flow, it doesn't fake around it.
//
// SAFETY: hard-blocked unless Nomba:BaseUrl is the sandbox host. Even if this
// endpoint ships to production by accident, it cannot move real money — it will
// always reject with a 409 against the live API.
// ══════════════════════════════════════════════════════════════════════════════

public record SimulateTransferCommand(Guid MemberId, decimal Amount, string? SenderName)
    : IRequest<SimulateTransferResult>;

public record SimulateTransferResult(
    Guid MemberId,
    string MemberName,
    string VirtualAccountNumber,
    decimal Amount,
    string TransferReference,
    string Status,
    string Message);

public class SimulateTransferHandler(
    AppDbContext db,
    INombaClient nomba,
    IOptions<NombaOptions> nombaOptions,
    ILogger<SimulateTransferHandler> logger)
    : IRequestHandler<SimulateTransferCommand, SimulateTransferResult>
{
    private const decimal SandboxMinimumAmount = 100m; // Nomba sandbox VA floor

    public async Task<SimulateTransferResult> Handle(SimulateTransferCommand cmd, CancellationToken ct)
    {
        // Guard 1: never allow this against anything but sandbox.
        var baseUrl = nombaOptions.Value.BaseUrl ?? string.Empty;
        if (!baseUrl.Contains("sandbox", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConflictException(
                "Transfer simulation is disabled — Nomba:BaseUrl is not pointed at sandbox. " +
                "This safeguard prevents real money movement if this endpoint is ever reachable in production.");
        }

        // Guard 2: sandbox VAs reject amounts below ₦100.
        if (cmd.Amount < SandboxMinimumAmount)
        {
            throw new ConflictException(
                $"Amount must be at least ₦{SandboxMinimumAmount:N0} — Nomba's sandbox virtual accounts reject smaller transfers.");
        }

        var member = await db.Members
            .Include(m => m.Circle)
            .FirstOrDefaultAsync(m => m.Id == cmd.MemberId, ct)
            ?? throw new NotFoundException(nameof(Member), cmd.MemberId);

        if (string.IsNullOrWhiteSpace(member.VirtualAccountNumber))
            throw new ConflictException($"Member {member.Name} has no virtual account provisioned yet.");

        var reference = $"SIM-{member.Id:N}-{DateTime.UtcNow:yyyyMMddHHmmss}"[..40];
        var senderName = string.IsNullOrWhiteSpace(cmd.SenderName) ? "Test Sender" : cmd.SenderName;

        logger.LogInformation(
            "Simulating inbound transfer: ₦{Amount} → {MemberName} ({Va}) ref {Ref}",
            cmd.Amount, member.Name, member.VirtualAccountNumber, reference);

        var transfer = await nomba.InitiateTransferAsync(new InitiateTransferRequest(
            AccountNumber: member.VirtualAccountNumber,
            BankCode: nombaOptions.Value.DefaultBankCode, // "000026" — Nomba MFB, same bank VAs live on
            Amount: cmd.Amount,
            Narration: $"Simulated contribution — {member.Circle.Name}",
            Reference: reference), ct);

        return new SimulateTransferResult(
            member.Id,
            member.Name,
            member.VirtualAccountNumber,
            cmd.Amount,
            transfer.TransferReference,
            transfer.Status,
            "Transfer sent. If the webhook is wired correctly, reconciliation " +
            "should land within a few seconds — check your logs for 'RAW NOMBA WEBHOOK'.");
    }
}

// ── Endpoint ──────────────────────────────────────────────────────────────────

public record SimulateTransferRequest(Guid MemberId, decimal Amount, string? SenderName);

public static class SimulateTransferEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/dev/simulate-transfer",
            async (SimulateTransferRequest body, IMediator mediator) =>
            {
                var result = await mediator.Send(
                    new SimulateTransferCommand(body.MemberId, body.Amount, body.SenderName));
                return Results.Ok(ApiResponse<SimulateTransferResult>.Ok(
                    result, "Simulated transfer sent to Nomba sandbox."));
            })
        .WithName("SimulateTransfer")
        .WithTags("Dev Tools")
        .AllowAnonymous();
}