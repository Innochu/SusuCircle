// ══════════════════════════════════════════════════════════════════════════════
// REQUIRES new columns on Member:
//     public string? PayoutBankAccountNumber { get; set; }
//     public string? PayoutBankCode { get; set; }
//     public string? PayoutBankName { get; set; }        // resolved name, from Nomba lookup
//     public string? PayoutBankLabel { get; set; }        // e.g. "GTBank" — display label, separate from Nomba's bank code
// Migrate: Add-Migration MemberPayoutAccount / dotnet ef migrations add MemberPayoutAccount
//
// FLOW:
//   1. Member submits {bankCode, bankLabel, accountNumber} — their REAL personal
//      bank account, NOT their contribution VA. This is the account payouts
//      will actually be sent to when it's their turn.
//   2. Backend resolves the real account name via Nomba's bank lookup and
//      returns it for confirmation — catches typos before anything is saved.
//   3. Only once resolved successfully is the payout account persisted.
//
// IMPORTANT: TriggerPayoutHandler must be updated separately to transfer to
// member.PayoutBankAccountNumber / PayoutBankCode instead of
// member.VirtualAccountNumber. Paying out to the member's own VA is WRONG —
// per Nomba's docs, funds sent to any Nomba VA just re-enter your wallet
// balance, meaning the payout would never actually leave your merchant
// account. TriggerPayoutHandler should also throw a clear ConflictException
// if a member's turn comes up and they haven't set a payout account yet,
// rather than silently paying the wrong destination.
// ══════════════════════════════════════════════════════════════════════════════

using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Nomba;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Features.Members.SetPayoutAccount;

// ── Submit / update payout bank account ─────────────────────────────────────

public record SetPayoutAccountCommand(
    Guid MemberId, string BankCode, string BankLabel, string AccountNumber)
    : IRequest<PayoutAccountDto>;

public record PayoutAccountDto(
    Guid MemberId,
    string BankLabel,
    string BankCode,
    string AccountNumber,      // masked for display, e.g. "****4185"
    string ResolvedAccountName);

public class SetPayoutAccountValidator : AbstractValidator<SetPayoutAccountCommand>
{
    public SetPayoutAccountValidator()
    {
        RuleFor(x => x.BankCode).NotEmpty();
        RuleFor(x => x.BankLabel).NotEmpty();
        RuleFor(x => x.AccountNumber).NotEmpty().Length(10)
            .WithMessage("Enter a valid 10-digit NUBAN account number.");
    }
}

public class SetPayoutAccountHandler(AppDbContext db, INombaClient nomba, ILogger<SetPayoutAccountHandler> logger)
    : IRequestHandler<SetPayoutAccountCommand, PayoutAccountDto>
{
    public async Task<PayoutAccountDto> Handle(SetPayoutAccountCommand cmd, CancellationToken ct)
    {
        var member = await db.Members.FindAsync([cmd.MemberId], ct)
            ?? throw new NotFoundException(nameof(Member), cmd.MemberId);

        // Resolve the real account name BEFORE saving anything — this is the
        // whole point: catch a typo'd account number now, not when a real
        // payout silently goes to the wrong person later.
        BankLookupResult lookup;
        try
        {
            lookup = await nomba.LookupBankAccountAsync(cmd.AccountNumber, cmd.BankCode, ct);
        }
        catch (NombaApiException ex)
        {
            logger.LogWarning(ex, "Bank lookup failed for member {MemberId}: {AccountNumber}/{BankCode}",
                cmd.MemberId, cmd.AccountNumber, cmd.BankCode);
            throw new ConflictException(
                "Could not verify that account number and bank — please double-check the details and try again.");
        }

        member.PayoutBankAccountNumber = cmd.AccountNumber;
        member.PayoutBankCode = cmd.BankCode;
        member.PayoutBankLabel = cmd.BankLabel;
        member.PayoutBankName = lookup.AccountName;

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Member {MemberId} set payout account: {Bank} ...{Last4} — {Name}",
            member.Id, cmd.BankLabel, cmd.AccountNumber[^4..], lookup.AccountName);

        return new PayoutAccountDto(
            member.Id, cmd.BankLabel, cmd.BankCode,
            Mask(cmd.AccountNumber), lookup.AccountName);
    }

    private static string Mask(string accountNumber) =>
        accountNumber.Length <= 4 ? accountNumber : $"****{accountNumber[^4..]}";
}

// ── Get current payout account (for display in the member portal) ──────────

public record GetPayoutAccountQuery(Guid MemberId) : IRequest<PayoutAccountDto?>;

public class GetPayoutAccountHandler(AppDbContext db) : IRequestHandler<GetPayoutAccountQuery, PayoutAccountDto?>
{
    public async Task<PayoutAccountDto?> Handle(GetPayoutAccountQuery q, CancellationToken ct)
    {
        var member = await db.Members.FindAsync([q.MemberId], ct)
            ?? throw new NotFoundException(nameof(Member), q.MemberId);

        if (string.IsNullOrWhiteSpace(member.PayoutBankAccountNumber))
            return null; // no payout account set yet — frontend should prompt to add one

        return new PayoutAccountDto(
            member.Id,
            member.PayoutBankLabel ?? "",
            member.PayoutBankCode ?? "",
            member.PayoutBankAccountNumber.Length <= 4
                ? member.PayoutBankAccountNumber
                : $"****{member.PayoutBankAccountNumber[^4..]}",
            member.PayoutBankName ?? "");
    }
}

// ── Endpoints ─────────────────────────────────────────────────────────────────

public record SetPayoutAccountRequest(string BankCode, string BankLabel, string AccountNumber);

public static class PayoutAccountEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/members/{memberId:guid}/payout-account",
            async (Guid memberId, SetPayoutAccountRequest body, IMediator mediator) =>
            {
                var result = await mediator.Send(
                    new SetPayoutAccountCommand(memberId, body.BankCode, body.BankLabel, body.AccountNumber));
                return Results.Ok(ApiResponse<PayoutAccountDto>.Ok(result,
                    $"Payout account verified: {result.ResolvedAccountName}"));
            })
            .WithName("SetMemberPayoutAccount")
            .WithTags("Members")
            .AllowAnonymous();

        app.MapGet("/api/members/{memberId:guid}/payout-account",
            async (Guid memberId, IMediator mediator) =>
            {
                var result = await mediator.Send(new GetPayoutAccountQuery(memberId));
                return Results.Ok(ApiResponse<PayoutAccountDto?>.Ok(result));
            })
            .WithName("GetMemberPayoutAccount")
            .WithTags("Members")
            .AllowAnonymous();
    }
}