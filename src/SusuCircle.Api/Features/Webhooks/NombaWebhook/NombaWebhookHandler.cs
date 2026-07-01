using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Nomba;
using SusuCircle.Api.Common.Persistence;
using SusuCircle.Api.Common.Services;
using SusuCircle.Api.Infrastructure;

namespace SusuCircle.Api.Features.Webhooks.NombaWebhook;

// ── Command ───────────────────────────────────────────────────────────────────

public record ProcessWebhookCommand(NombaWebhookPayload Payload) : IRequest<WebhookResult>;

public record WebhookResult(bool Processed, string Message);

// ── Reconciliation Engine ─────────────────────────────────────────────────────

/// <summary>
/// Core reconciliation engine.
///
/// CORRECTED against the real Nomba webhook contract
/// (https://developer.nomba.com/products/webhooks/introduction):
///   - event_type "payment_success" is what fires for an inbound VA credit —
///     there is no separate "virtual_account.funded" or "INWARD_TRANSFER" event.
///   - The VA that received the money is data.transaction.aliasAccountNumber
///     (data.customer.accountNumber is a DIFFERENT field — the sender/payer's
///     own account, not the receiving VA — so matching on customer.accountNumber
///     would silently match the wrong side of the transfer).
///   - Amount is data.transaction.transactionAmount, in plain NAIRA (verified:
///     the docs' own example pairs "transactionAmount": 1000 with "fee": 5 — a
///     0.5% fee only makes sense in naira, and the VA-creation endpoint uses
///     "expectedAmount": "200.00" — a decimal naira string, not kobo).
///   - Idempotency uses requestId (Nomba's documented dedupe key for webhook
///     retries), NOT the transaction reference alone — a retried delivery of the
///     same event reuses the same requestId.
///
/// On inbound transfer (payment_success):
///  1. Match aliasAccountNumber → member's VirtualAccountNumber
///  2. Find active contribution for member + current cycle
///  3. Idempotency check on requestId
///  4. paidAmount += amount
///  5. balance = expected - creditApplied - paidAmount
///      = 0    → Paid
///      > 0    → Partial (notify shortfall)
///      < 0    → Overpaid (credit excess to next cycle)
///  6. Trigger real-time SignalR push and automated transaction receipts email.
/// </summary>
public class NombaWebhookHandler(
    AppDbContext db,
    INotificationService notifications,
    IHubContext<CircleHub> hub,
    ICreditScoreService creditScore,
    ILogger<NombaWebhookHandler> logger)
    : IRequestHandler<ProcessWebhookCommand, WebhookResult>
{
    public async Task<WebhookResult> Handle(ProcessWebhookCommand cmd, CancellationToken ct)
    {
        var payload = cmd.Payload;

        // CORRECTED: real event type is "payment_success", not "INWARD_TRANSFER".
        // We only care about money landing in a member's VA here; payouts
        // (payout_success / payout_failed) are handled by a separate branch below.
        if (payload.EventType is "payout_success" or "payout_failed" or "payout_refund")
        {
            return await HandlePayoutEventAsync(payload, ct);
        }

        if (payload.EventType != "payment_success")
        {
            logger.LogInformation("Skipping non-payment webhook event: {EventType}", payload.EventType);
            return new WebhookResult(false, "Event type not handled.");
        }

        // CORRECTED: the receiving VA number is on the transaction object,
        // not the customer object (customer.accountNumber is the payer's account).
        var receivingAccountNumber = payload.Data.Transaction.AliasAccountNumber;

        if (string.IsNullOrWhiteSpace(receivingAccountNumber))
        {
            logger.LogWarning("Webhook: payment_success with no aliasAccountNumber. RequestId {RequestId}",
                payload.RequestId);
            return new WebhookResult(false, "No receiving account number on payload.");
        }

        // Step 1: Resolve member by virtual account number
        var member = await db.Members
            .Include(m => m.Circle)
            .FirstOrDefaultAsync(m => m.VirtualAccountNumber == receivingAccountNumber, ct);

        if (member is null)
        {
            logger.LogWarning("Webhook: No member found for account {AccountNumber}", receivingAccountNumber);
            return new WebhookResult(false, "Account number not found.");
        }

        if (member.Status != MemberStatus.Active)
        {
            logger.LogWarning("Webhook: Member {MemberId} is not active.", member.Id);
            return new WebhookResult(false, "Member is not active.");
        }

        var circle = member.Circle;

        if (circle.Status != CircleStatus.Active)
            return new WebhookResult(false, "Circle is not active.");

        // Step 2: Find open contribution
        var contribution = await db.Contributions
            .FirstOrDefaultAsync(c =>
                c.MemberId == member.Id &&
                c.CycleNumber == circle.CurrentCycle &&
                c.Status != ContributionStatus.Paid &&
                c.Status != ContributionStatus.Overpaid, ct);

        if (contribution is null)
        {
            logger.LogWarning("Webhook: No open contribution for member {MemberId} cycle {Cycle}", member.Id, circle.CurrentCycle);
            return new WebhookResult(false, "No open contribution for current cycle.");
        }

        // Step 3: Idempotency check — CORRECTED to use requestId, Nomba's
        // documented dedupe key for webhook retries (not transactionId alone).
        var duplicate = await db.Contributions
            .AnyAsync(c => c.NombaTransactionRef == payload.RequestId, ct);

        if (duplicate)
        {
            logger.LogWarning("Webhook: Duplicate requestId {RequestId}", payload.RequestId);
            return new WebhookResult(false, "Duplicate request — already processed.");
        }

        // Step 4 & 5: Reconcile
        var amount = payload.Data.Transaction.TransactionAmount; // plain naira

        contribution.PaidAmount += amount;
        contribution.NombaTransactionRef = payload.RequestId;
        contribution.PaidAt = payload.Data.Transaction.Time;

        var balance = contribution.ExpectedAmount - contribution.CreditApplied - contribution.PaidAmount;

        string notifTitle;
        string notifBody;

        if (balance == 0)
        {
            contribution.Status = ContributionStatus.Paid;
            UpdateStreak(member, onTime: contribution.PaidAt <= contribution.DueDate);
            notifTitle = "Payment confirmed ✅";
            notifBody = $"Your contribution of ₦{contribution.PaidAmount:N0} for cycle {circle.CurrentCycle} has been received in full.";
            logger.LogInformation("Member {MemberId} PAID cycle {Cycle}", member.Id, circle.CurrentCycle);
        }
        else if (balance > 0)
        {
            contribution.Status = ContributionStatus.Partial;
            notifTitle = "Partial payment received ⚠️";
            notifBody = $"₦{contribution.PaidAmount:N0} received. You still owe ₦{balance:N0} for cycle {circle.CurrentCycle}. Please pay before {contribution.DueDate:dd MMM yyyy}.";
            logger.LogInformation("Member {MemberId} PARTIAL cycle {Cycle} — balance ₦{Balance}", member.Id, circle.CurrentCycle, balance);
        }
        else
        {
            var excess = Math.Abs(balance);
            contribution.Status = ContributionStatus.Overpaid;
            UpdateStreak(member, onTime: contribution.PaidAt <= contribution.DueDate);

            await ApplyCreditToNextCycleAsync(member.Id, circle, excess, ct);

            notifTitle = "Payment confirmed + credit applied ✅";
            notifBody = $"₦{contribution.PaidAmount:N0} received. You overpaid by ₦{excess:N0} — this has been credited to your next cycle.";
            logger.LogInformation("Member {MemberId} OVERPAID cycle {Cycle} — credit ₦{Excess}", member.Id, circle.CurrentCycle, excess);
        }

        await db.SaveChangesAsync(ct);

        await notifications.SendAsync(member.Id, NotificationType.PaymentReceived, notifTitle, notifBody, ct);

        if (!string.IsNullOrWhiteSpace(member.Email))
        {
            await notifications.SendCircleCreditedEmailAsync(
                member.Email, member.Name, circle.Name, amount, contribution.Status.ToString());
        }

        if (circle.Plan == PlanType.ADASHI)
            await creditScore.RecalculateAsync(member.Id, ct);

        await CheckAndTriggerPayoutAsync(circle, ct);

        await hub.Clients.Group(circle.Id.ToString())
            .SendAsync("ContributionUpdated", new
            {
                circleId = circle.Id,
                memberId = member.Id,
                memberName = member.Name,
                cycleNumber = circle.CurrentCycle,
                status = contribution.Status.ToString(),
                paidAmount = contribution.PaidAmount,
                balance,
            }, ct);

        return new WebhookResult(true, "Reconciled successfully.");
    }

    // ── Payout confirmation events ──────────────────────────────────────────────
    // NEW: payout_success / payout_failed / payout_refund arrive as separate
    // webhook events. Previously nothing listened for these — payouts were only
    // ever assumed complete from the synchronous transfer API response, which
    // can return PENDING. This closes that gap.
    private async Task<WebhookResult> HandlePayoutEventAsync(NombaWebhookPayload payload, CancellationToken ct)
    {
        var merchantTxRef = payload.Data.Transaction.MerchantTxRef;
        if (string.IsNullOrWhiteSpace(merchantTxRef))
        {
            logger.LogWarning("Webhook: {EventType} with no merchantTxRef. RequestId {RequestId}",
                payload.EventType, payload.RequestId);
            return new WebhookResult(false, "No merchantTxRef on payload.");
        }

        var payout = await db.Payouts
            .FirstOrDefaultAsync(p => p.NombaTransferRef == merchantTxRef, ct);

        if (payout is null)
        {
            logger.LogWarning("Webhook: No payout found for merchantTxRef {Ref}", merchantTxRef);
            return new WebhookResult(false, "Payout not found for reference.");
        }

        switch (payload.EventType)
        {
            case "payout_success":
                payout.Status = PayoutStatus.Completed;
                payout.DisbursedAmount = payload.Data.Transaction.TransactionAmount;
                payout.DisbursedAt = payload.Data.Transaction.Time;
                break;
            case "payout_failed":
                payout.Status = PayoutStatus.Failed;
                payout.FailureReason = "Payout failed per Nomba webhook.";
                payout.RetryCount++;
                break;
            case "payout_refund":
                payout.Status = PayoutStatus.Failed;
                payout.FailureReason = "Payout was refunded back to merchant account.";
                break;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Payout {PayoutId} updated to {Status} via webhook", payout.Id, payout.Status);

        return new WebhookResult(true, $"Payout {payload.EventType} processed.");
    }

    private static void UpdateStreak(Member member, bool onTime)
    {
        if (onTime) member.ConsecutiveOnTimeStreak++;
        else member.ConsecutiveOnTimeStreak = 0;
    }

    private async Task ApplyCreditToNextCycleAsync(Guid memberId, Circle circle, decimal excess, CancellationToken ct)
    {
        var nextCycle = circle.CurrentCycle + 1;
        var nextContribution = await db.Contributions
            .FirstOrDefaultAsync(c => c.MemberId == memberId && c.CycleNumber == nextCycle, ct);

        if (nextContribution is not null)
        {
            nextContribution.CreditApplied += excess;
        }
        else
        {
            db.Contributions.Add(new Contribution
            {
                Id = Guid.NewGuid(),
                MemberId = memberId,
                CircleId = circle.Id,
                CycleNumber = nextCycle,
                ExpectedAmount = circle.ContributionAmount,
                CreditApplied = excess,
                DueDate = circle.NextContributionDate.AddMonths(1),
            });
        }
    }

    private async Task CheckAndTriggerPayoutAsync(Circle circle, CancellationToken ct)
    {
        var activeMembers = await db.Members
            .Where(m => m.CircleId == circle.Id && m.Status == MemberStatus.Active)
            .CountAsync(ct);

        var cycleContributions = await db.Contributions
            .Where(c => c.CircleId == circle.Id && c.CycleNumber == circle.CurrentCycle)
            .ToListAsync(ct);

        bool payoutReady = circle.Plan switch
        {
            PlanType.BAM => cycleContributions.All(c =>
                c.Status is ContributionStatus.Paid or ContributionStatus.Overpaid),
            PlanType.ADASHI => cycleContributions.Sum(c => c.PaidAmount) >= circle.ContributionAmount * activeMembers,
            _ => false
        };

        if (!payoutReady) return;

        var existing = await db.Payouts
            .AnyAsync(p => p.CircleId == circle.Id && p.CycleNumber == circle.CurrentCycle, ct);

        if (existing) return;

        Member? payoutMember = null;

        if (circle.Plan == PlanType.BAM)
        {
            payoutMember = await db.Members
                .Where(m => m.CircleId == circle.Id && m.Status == MemberStatus.Active && m.PayoutPosition == 0)
                .FirstOrDefaultAsync(ct);
        }
        else if (circle.Plan == PlanType.ADASHI)
        {
            payoutMember = await db.Members
                .Where(m => m.CircleId == circle.Id && m.Status == MemberStatus.Active)
                .OrderBy(m => m.PayoutPosition)
                .Skip(circle.CurrentCycle - 1)
                .FirstOrDefaultAsync(ct);
        }

        if (payoutMember is null) return;

        db.Payouts.Add(new Payout
        {
            Id = Guid.NewGuid(),
            CircleId = circle.Id,
            MemberId = payoutMember.Id,
            CycleNumber = circle.CurrentCycle,
            ExpectedAmount = circle.ContributionAmount * activeMembers,
            DisbursedAmount = 0,
            Status = PayoutStatus.Pending,
            ScheduledAt = DateTime.UtcNow,
        });

        logger.LogInformation("Payout queued for member {MemberId} circle {CircleId} cycle {Cycle}", payoutMember.Id, circle.Id, circle.CurrentCycle);
    }
}

// ── Endpoint ──────────────────────────────────────────────────────────────────

public sealed class NombaWebhookEndpointMarker { }
public static class NombaWebhookEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/webhooks/nomba",
            async (HttpRequest req, IMediator mediator, INombaClient nomba, ILoggerFactory loggerFactory) =>
            {
                var logger = loggerFactory.CreateLogger<NombaWebhookEndpointMarker>();

                req.EnableBuffering();
                using var reader = new System.IO.StreamReader(req.Body, leaveOpen: true);
                var rawBody = await reader.ReadToEndAsync();
                req.Body.Position = 0;

                // TEMPORARY — remove once the payload shape is confirmed against
                // NombaWebhookPayload.cs. LogWarning (not LogInformation) so it's
                // easy to spot/filter in Render's log viewer. This logs the FULL
                // raw payload, so don't leave it in past the verification step.
                logger.LogWarning("RAW NOMBA WEBHOOK: {Body}", rawBody);

                // CORRECTED: real header is "nomba-signature" (lowercase, hyphenated),
                // not "X-Nomba-Signature". ASP.NET header lookup is case-insensitive,
                // but the NAME itself was wrong, so this always returned empty before.
                var signature = req.Headers["nomba-signature"].FirstOrDefault() ?? string.Empty;

                if (!nomba.VerifyWebhookSignature(rawBody, signature))
                {
                    logger.LogWarning("Webhook signature verification failed.");
                    return Results.Unauthorized();
                }

                var payload = System.Text.Json.JsonSerializer.Deserialize<NombaWebhookPayload>(rawBody,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (payload is null) return Results.BadRequest("Invalid payload.");

                var result = await mediator.Send(new ProcessWebhookCommand(payload));
                return Results.Ok(result);
            })
        .WithName("NombaWebhook")
        .WithTags("Webhooks")
        .AllowAnonymous(); // Auth is handled securely via HMAC signature validation
}