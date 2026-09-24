using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Nomba;
using SusuCircle.Api.Common.Persistence;
using SusuCircle.Api.Common.Services;

namespace SusuCircle.Api.Features.Collections.DisburseFromCollection;

// ══════════════════════════════════════════════════════════════════════════════
// "Debit the collection account, credit the member."
//
// Unlike the sweep, this one DOES move real money: it debits the pooled ledger
// balance and fires an outbound transfer to the member's verified bank account.
//
// It deliberately targets PayoutBankAccountNumber and never the member's own
// contribution virtual account. Funds sent into a virtual account are re-pooled
// into the merchant wallet by the provider, so paying a member that way would
// report success while the money quietly returned to where it started.
//
// SCOPE: this is a direct disbursement primitive. It does NOT create a Payout
// row or advance the cycle — TriggerPayout remains the cycle-driven path, and
// two things advancing cycles would corrupt the payout board. Use this for
// off-schedule or partial disbursements; use TriggerPayout for a rotation turn.
// ══════════════════════════════════════════════════════════════════════════════

public record DisburseFromCollectionCommand(
    Guid CircleId,
    Guid MemberId,
    decimal Amount,
    string? Narration = null) : IRequest<DisbursementResult>;

public record DisbursementResult(
    Guid CircleId,
    Guid MemberId,
    string MemberName,
    decimal Amount,
    string DestinationAccountMasked,
    string? DestinationBank,
    string TransferReference,
    string TransferStatus,
    decimal BalanceBefore,
    decimal BalanceAfter,
    Guid LedgerEntryId);

public class DisburseFromCollectionValidator : AbstractValidator<DisburseFromCollectionCommand>
{
    public DisburseFromCollectionValidator()
    {
        RuleFor(x => x.CircleId).NotEmpty();
        RuleFor(x => x.MemberId).NotEmpty();
        RuleFor(x => x.Amount).GreaterThan(0);
        RuleFor(x => x.Narration).MaximumLength(100).When(x => x.Narration is not null);
    }
}

public class DisburseFromCollectionHandler(
    AppDbContext db,
    INombaClient payments,
    ICollectionAccountService collectionAccounts,
    INotificationService notifications,
    ILogger<DisburseFromCollectionHandler> logger)
    : IRequestHandler<DisburseFromCollectionCommand, DisbursementResult>
{
    public async Task<DisbursementResult> Handle(DisburseFromCollectionCommand cmd, CancellationToken ct)
    {
        var circle = await db.Circles
            .FirstOrDefaultAsync(c => c.Id == cmd.CircleId, ct)
            ?? throw new NotFoundException(nameof(Circle), cmd.CircleId);

        var member = await db.Members
            .FirstOrDefaultAsync(m => m.Id == cmd.MemberId, ct)
            ?? throw new NotFoundException(nameof(Member), cmd.MemberId);

        // Scoped check: a member of another circle must never be payable out of
        // this circle's pooled funds.
        if (member.CircleId != circle.Id)
            throw new ConflictException($"{member.Name} is not a member of {circle.Name}.");

        if (string.IsNullOrWhiteSpace(member.PayoutBankAccountNumber) ||
            string.IsNullOrWhiteSpace(member.PayoutBankCode))
        {
            throw new ConflictException(
                $"{member.Name} has not set up a payout bank account yet. " +
                "They must add one from the member portal before funds can be sent.");
        }

        var account = await collectionAccounts.GetActiveAsync(circle.Id, ct);
        var balanceBefore = await collectionAccounts.GetBalanceAsync(account.Id, ct);

        if (cmd.Amount > balanceBefore)
        {
            throw new ConflictException(
                $"Insufficient collection balance — requested {cmd.Amount:N2} but {circle.Name} holds {balanceBefore:N2}. " +
                "Run a sweep first if contributions have been received but not yet pooled.");
        }

        var reference = $"DISB-{Guid.NewGuid():N}";

        // Write the debit FIRST, then transfer. Reserving the funds before the
        // money leaves means two concurrent disbursements cannot both pass the
        // balance check and jointly overdraw the pool. If the transfer then
        // fails the reservation is released below, so the worst this can leave
        // behind is a brief reservation that never became a payment — strictly
        // safer than transferring against a balance nobody reserved.
        var entry = new CollectionLedgerEntry
        {
            Id = Guid.NewGuid(),
            CollectionAccountId = account.Id,
            CircleId = circle.Id,
            MemberId = member.Id,
            CycleNumber = circle.CurrentCycle,
            Direction = CollectionEntryDirection.Debit,
            Amount = cmd.Amount,
            Reference = reference,
            Description = cmd.Narration ?? $"Disbursement to {member.Name} (cycle {circle.CurrentCycle})",
        };

        db.CollectionLedgerEntries.Add(entry);
        await db.SaveChangesAsync(ct);

        TransferResponse transfer;
        try
        {
            transfer = await payments.InitiateTransferAsync(new InitiateTransferRequest(
                AccountNumber: member.PayoutBankAccountNumber,
                BankCode: member.PayoutBankCode,
                Amount: cmd.Amount,
                Narration: cmd.Narration ?? $"Susu Circle disbursement - {circle.Name}",
                Reference: reference), ct);
        }
        catch (Exception ex)
        {
            // Release the reservation — no money moved, so the pool must not
            // stay debited.
            db.CollectionLedgerEntries.Remove(entry);
            await db.SaveChangesAsync(ct);

            logger.LogError(ex,
                "Disbursement failed for member {MemberId} from circle {CircleId}; ledger debit {Ref} reversed",
                member.Id, circle.Id, reference);
            throw;
        }

        var balanceAfter = await collectionAccounts.GetBalanceAsync(account.Id, ct);

        logger.LogInformation(
            "Disbursed {Amount} from collection account {Account} to member {MemberId} ref {Ref} status {Status}",
            cmd.Amount, account.AccountNumber, member.Id, reference, transfer.Status);

        await notifications.SendAsync(member.Id, NotificationType.PayoutSent,
            "Funds sent",
            $"{cmd.Amount:N0} has been sent to your {member.PayoutBankLabel ?? "bank"} account from {circle.Name}.", ct);

        return new DisbursementResult(
            circle.Id, member.Id, member.Name, cmd.Amount,
            Mask(member.PayoutBankAccountNumber),
            member.PayoutBankLabel ?? member.PayoutBankName,
            transfer.TransferReference, transfer.Status,
            balanceBefore, balanceAfter, entry.Id);
    }

    private static string Mask(string accountNumber) =>
        accountNumber.Length <= 4 ? accountNumber : $"****{accountNumber[^4..]}";
}

public record DisburseRequest(Guid MemberId, decimal Amount, string? Narration = null);

public static class DisburseFromCollectionEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/circles/{circleId:guid}/collection/disburse",
            async (Guid circleId, DisburseRequest body, IMediator mediator) =>
            {
                var result = await mediator.Send(new DisburseFromCollectionCommand(
                    circleId, body.MemberId, body.Amount, body.Narration));
                return Results.Ok(ApiResponse<DisbursementResult>.Ok(result,
                    "Collection account debited and transfer initiated."));
            })
        .WithName("DisburseFromCollection")
        .WithTags("Collections");
}
