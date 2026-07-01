using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Nomba;
using SusuCircle.Api.Common.Persistence;
using SusuCircle.Api.Common.Services;

namespace SusuCircle.Api.Features.Payouts.TriggerPayout;

public record TriggerPayoutCommand(Guid CircleId, bool AdminOverride = false) : IRequest<PayoutDto>;

public record PayoutDto(
    Guid Id, Guid CircleId, Guid MemberId, string MemberName,
    int CycleNumber, decimal Amount, PayoutStatus Status, DateTime ScheduledAt);

public class TriggerPayoutValidator : AbstractValidator<TriggerPayoutCommand>
{
    public TriggerPayoutValidator() => RuleFor(x => x.CircleId).NotEmpty();
}

public class TriggerPayoutHandler(
    AppDbContext db, INombaClient nomba,
    INotificationService notifications,
    ILogger<TriggerPayoutHandler> logger)
    : IRequestHandler<TriggerPayoutCommand, PayoutDto>
{
    public async Task<PayoutDto> Handle(TriggerPayoutCommand cmd, CancellationToken ct)
    {
        var circle = await db.Circles
            .Include(c => c.Members.Where(m => m.Status == MemberStatus.Active))
            .Include(c => c.Payouts)
            .FirstOrDefaultAsync(c => c.Id == cmd.CircleId, ct)
            ?? throw new NotFoundException(nameof(Circle), cmd.CircleId);

        // Validate no payout already done for this cycle
        if (circle.Payouts.Any(p => p.CycleNumber == circle.CurrentCycle && p.Status == PayoutStatus.Completed))
            throw new ConflictException($"Payout for cycle {circle.CurrentCycle} already completed.");

        // Find or get pending payout record
        var payout = circle.Payouts.FirstOrDefault(p => p.CycleNumber == circle.CurrentCycle && p.Status == PayoutStatus.Pending)
            ?? await CreatePayoutRecordAsync(circle, ct);

        var payoutMember = circle.Members.First(m => m.Id == payout.MemberId);

        if (string.IsNullOrEmpty(payoutMember.VirtualAccountNumber))
            throw new ConflictException("Payout member has no virtual account.");

        // Update status to processing
        payout.Status = PayoutStatus.Processing;
        await db.SaveChangesAsync(ct);

        try
        {
            var transferRef = $"PAYOUT-{circle.Id}-CYC{circle.CurrentCycle}-{Guid.NewGuid():N}"[..40];

            var transfer = await nomba.InitiateTransferAsync(new InitiateTransferRequest(
                AccountNumber: payoutMember.VirtualAccountNumber,
                BankCode: "000026", // Nomba MFB code
                Amount: payout.ExpectedAmount,
                Narration: $"Susu Circle payout – {circle.Name} Cycle {circle.CurrentCycle}",
                Reference: transferRef), ct);

            // CORRECTED: store the merchantTxRef WE sent (transferRef), not Nomba's
            // own transfer id (transfer.TransferReference, which maps from data.id).
            // The inbound payout_success/payout_failed webhook reports back
            // data.transaction.merchantTxRef — that's OUR reference, so this is the
            // only value NombaWebhookHandler.HandlePayoutEventAsync can match against.
            payout.NombaTransferRef = transferRef;

            // CORRECTED: a transfer can come back PENDING — the real outcome then
            // arrives later via the payout_success/payout_failed webhook. Only mark
            // Completed and advance the cycle when Nomba's synchronous response
            // itself says success; otherwise leave it Processing and let the
            // webhook (HandlePayoutEventAsync) finish the job.
            var isImmediateSuccess = string.Equals(transfer.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase);

            if (isImmediateSuccess)
            {
                payout.Status = PayoutStatus.Completed;
                payout.DisbursedAmount = payout.ExpectedAmount;
                payout.DisbursedAt = DateTime.UtcNow;

                await db.SaveChangesAsync(ct);

                await notifications.SendAsync(payoutMember.Id, NotificationType.PayoutSent,
                    "Payout sent! 🎉",
                    $"₦{payout.DisbursedAmount:N0} has been sent to your account for cycle {circle.CurrentCycle} of '{circle.Name}'.", ct);

                await AdvanceCycleAsync(circle, ct);

                logger.LogInformation("Payout completed for member {MemberId} cycle {Cycle} — ₦{Amount}",
                    payoutMember.Id, circle.CurrentCycle, payout.DisbursedAmount);
            }
            else
            {
                // Stays PayoutStatus.Processing. The cycle is NOT advanced yet —
                // doing so now would be wrong if the transfer later fails.
                // TODO: when the payout_success webhook confirms this reference,
                // AdvanceCycleAsync needs to run at that point too. Right now
                // NombaWebhookHandler.HandlePayoutEventAsync only updates the
                // Payout row's status — it doesn't advance the circle's cycle.
                // That logic should move into a shared method both handlers call,
                // so a PENDING-then-webhook-confirmed payout still advances the
                // circle exactly once.
                await db.SaveChangesAsync(ct);

                logger.LogInformation(
                    "Payout for member {MemberId} cycle {Cycle} is {Status} — awaiting webhook confirmation (ref {Ref}).",
                    payoutMember.Id, circle.CurrentCycle, transfer.Status, transferRef);
            }
        }
        catch (NombaApiException ex)
        {
            payout.Status = PayoutStatus.Failed;
            payout.FailureReason = ex.Message;
            payout.RetryCount++;
            await db.SaveChangesAsync(ct);
            logger.LogError(ex, "Payout failed for member {MemberId} cycle {Cycle}", payoutMember.Id, circle.CurrentCycle);
            throw;
        }

        return new PayoutDto(payout.Id, payout.CircleId, payout.MemberId, payoutMember.Name,
            payout.CycleNumber, payout.DisbursedAmount, payout.Status, payout.ScheduledAt);
    }

    private async Task<Payout> CreatePayoutRecordAsync(Circle circle, CancellationToken ct)
    {
        var payoutMember = circle.Members
            .OrderBy(m => m.PayoutPosition)
            .Skip(circle.CurrentCycle - 1)
            .First();

        var totalCollected = await db.Contributions
            .Where(c => c.CircleId == circle.Id && c.CycleNumber == circle.CurrentCycle)
            .SumAsync(c => c.PaidAmount, ct);

        var payout = new Payout
        {
            Id = Guid.NewGuid(),
            CircleId = circle.Id,
            MemberId = payoutMember.Id,
            CycleNumber = circle.CurrentCycle,
            ExpectedAmount = totalCollected,
            Status = PayoutStatus.Pending,
            ScheduledAt = DateTime.UtcNow,
        };

        db.Payouts.Add(payout);
        await db.SaveChangesAsync(ct);
        return payout;
    }

    private async Task AdvanceCycleAsync(Circle circle, CancellationToken ct)
    {
        circle.CurrentCycle++;
        circle.NextContributionDate = circle.Frequency switch
        {
            ContributionFrequency.Weekly => circle.NextContributionDate.AddDays(7),
            ContributionFrequency.Biweekly => circle.NextContributionDate.AddDays(14),
            _ => circle.NextContributionDate.AddMonths(1),
        };

        // Check if all members have received their payout
        if (circle.CurrentCycle > circle.Members.Count)
        {
            circle.Status = CircleStatus.Completed;
            logger.LogInformation("Circle {CircleId} completed all cycles.", circle.Id);
        }
        else
        {
            // Create contribution records for all active members for new cycle
            foreach (var member in circle.Members.Where(m => m.Status == MemberStatus.Active))
            {
                // Check if there's already a pre-created record (e.g. from credit carry-over)
                var existing = await db.Contributions
                    .FirstOrDefaultAsync(c => c.MemberId == member.Id && c.CycleNumber == circle.CurrentCycle, ct);

                if (existing is null)
                {
                    db.Contributions.Add(new Contribution
                    {
                        Id = Guid.NewGuid(),
                        MemberId = member.Id,
                        CircleId = circle.Id,
                        CycleNumber = circle.CurrentCycle,
                        ExpectedAmount = circle.ContributionAmount,
                        DueDate = circle.NextContributionDate,
                    });
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }
}

public static class TriggerPayoutEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/circles/{circleId:guid}/payout",
            async (Guid circleId, IMediator mediator, bool adminOverride = false) =>
            {
                var result = await mediator.Send(new TriggerPayoutCommand(circleId, adminOverride));
                return Results.Ok(ApiResponse<PayoutDto>.Ok(result, "Payout initiated."));
            })
        .WithName("TriggerPayout")
        .WithTags("Payouts")
        .AllowAnonymous();
}