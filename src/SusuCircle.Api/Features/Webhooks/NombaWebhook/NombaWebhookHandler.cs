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
/// Core reconciliation engine — implements the decision tree from the FRD.
/// 
/// On inbound transfer:
///  1. Match VA number → member
///  2. Find active contribution for member + current cycle
///  3. Idempotency check on transaction reference
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

        if (payload.EventType != "INWARD_TRANSFER")
        {
            logger.LogInformation("Skipping non-transfer webhook event: {EventType}", payload.EventType);
            return new WebhookResult(false, "Event type not handled.");
        }

        // Step 1: Resolve member by virtual account number
        var member = await db.Members
            .Include(m => m.Circle)
            .FirstOrDefaultAsync(m => m.VirtualAccountNumber == payload.AccountNumber, ct);

        if (member is null)
        {
            logger.LogWarning("Webhook: No member found for account {AccountNumber}", payload.AccountNumber);
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

        // Step 3: Idempotency check
        var duplicate = await db.Contributions
            .AnyAsync(c => c.NombaTransactionRef == payload.TransactionReference, ct);

        if (duplicate)
        {
            logger.LogWarning("Webhook: Duplicate transaction ref {Ref}", payload.TransactionReference);
            return new WebhookResult(false, "Duplicate transaction reference — already processed.");
        }

        // Step 4 & 5: Reconcile
        contribution.PaidAmount += payload.Amount;
        contribution.NombaTransactionRef = payload.TransactionReference;
        contribution.PaidAt = payload.Timestamp;

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
            // Overpaid — credit excess to next cycle
            var excess = Math.Abs(balance);
            contribution.Status = ContributionStatus.Overpaid;
            UpdateStreak(member, onTime: contribution.PaidAt <= contribution.DueDate);

            await ApplyCreditToNextCycleAsync(member.Id, circle, excess, ct);

            notifTitle = "Payment confirmed + credit applied ✅";
            notifBody = $"₦{contribution.PaidAmount:N0} received. You overpaid by ₦{excess:N0} — this has been credited to your next cycle.";
            logger.LogInformation("Member {MemberId} OVERPAID cycle {Cycle} — credit ₦{Excess}", member.Id, circle.CurrentCycle, excess);
        }

        await db.SaveChangesAsync(ct);

        // Notify member via internal notification system
        await notifications.SendAsync(member.Id, NotificationType.PaymentReceived, notifTitle, notifBody, ct);

        // REQUIREMENT: Email send to member when a circle collection has been successfully credited
        if (!string.IsNullOrWhiteSpace(member.Email))
        {
            await notifications.SendCircleCreditedEmailAsync(
                member.Email,
                member.Name,
                circle.Name,
                payload.Amount,
                contribution.Status.ToString()
            );
        }

        // Step 6: ADASHI credit score recalculation
        if (circle.Plan == PlanType.ADASHI)
            await creditScore.RecalculateAsync(member.Id, ct);

        // Check if payout should be triggered for this cycle
        await CheckAndTriggerPayoutAsync(circle, ct);

        // SignalR push to live dashboard
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
            // Pre-create next cycle contribution with credit applied
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
            // BAM: everyone must be fully paid
            PlanType.BAM => cycleContributions.All(c =>
                c.Status is ContributionStatus.Paid or ContributionStatus.Overpaid),

            // ADASHI: total collected >= expected total
            PlanType.ADASHI => cycleContributions.Sum(c => c.PaidAmount) >= circle.ContributionAmount * activeMembers,

            _ => false
        };

        if (!payoutReady) return;

        // Check payout hasn't already been created for this cycle
        var existing = await db.Payouts
            .AnyAsync(p => p.CircleId == circle.Id && p.CycleNumber == circle.CurrentCycle, ct);

        if (existing) return;

        // REQUIREMENT: Correctly select the eligible member based on BAM vs ADASHI plan mechanics
        Member? payoutMember = null;

        if (circle.Plan == PlanType.BAM)
        {
            // For BAM, payout positions are forced to 0. The payout target is designated via group admin 
            // or defaults atomically to the pool escrow/admin placeholder account.
            payoutMember = await db.Members
                .Where(m => m.CircleId == circle.Id && m.Status == MemberStatus.Active && m.PayoutPosition == 0)
                .FirstOrDefaultAsync(ct);
        }
        else if (circle.Plan == PlanType.ADASHI)
        {
            // For ADASHI, positions scale sequentially (1, 2, 3...). Target maps dynamically to the active index cycle.
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

                // Read raw body for HMAC verification
                req.EnableBuffering();
                using var reader = new System.IO.StreamReader(req.Body, leaveOpen: true);
                var rawBody = await reader.ReadToEndAsync();
                req.Body.Position = 0;

                var signature = req.Headers["X-Nomba-Signature"].FirstOrDefault() ?? string.Empty;

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