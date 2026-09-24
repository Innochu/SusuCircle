using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Features.Collections.ListMemberAccounts;

// ══════════════════════════════════════════════════════════════════════════════
// Every account number in one circle, in one call: the inbound contribution
// virtual account for each member, plus whether a payout bank account is set.
//
// The two are different things and are deliberately reported side by side. A
// virtual account only ever RECEIVES contributions; payouts go to the separate
// verified bank account, because money sent back into a virtual account is
// re-pooled into the merchant wallet and never reaches the member.
//
// Payout account numbers are masked to the last 4 — this is an admin-facing
// roster, not a place to re-expose personal bank details in full.
// ══════════════════════════════════════════════════════════════════════════════

public record ListMemberAccountsQuery(Guid CircleId, bool IncludeInactive = false)
    : IRequest<MemberAccountsDto>;

public record MemberAccountsDto(
    Guid CircleId,
    string CircleName,
    int MemberCount,
    int WithVirtualAccount,
    int ReadyForPayout,
    IReadOnlyList<MemberAccountDto> Members);

public record MemberAccountDto(
    Guid MemberId,
    string Name,
    string Phone,
    string? Email,
    int PayoutPosition,
    MemberStatus Status,
    string? VirtualAccountNumber,
    string? VirtualAccountBank,
    bool HasPayoutAccount,
    string? PayoutAccountMasked,
    string? PayoutBankLabel);

public class ListMemberAccountsHandler(AppDbContext db)
    : IRequestHandler<ListMemberAccountsQuery, MemberAccountsDto>
{
    public async Task<MemberAccountsDto> Handle(ListMemberAccountsQuery q, CancellationToken ct)
    {
        var circle = await db.Circles
            .FirstOrDefaultAsync(c => c.Id == q.CircleId, ct)
            ?? throw new NotFoundException(nameof(Circle), q.CircleId);

        var members = await db.Members
            .Where(m => m.CircleId == circle.Id && (q.IncludeInactive || m.Status == MemberStatus.Active))
            .OrderBy(m => m.PayoutPosition)
            .ThenBy(m => m.JoinedAt)
            .ToListAsync(ct);

        var dtos = members.Select(m => new MemberAccountDto(
            m.Id, m.Name, m.Phone, m.Email, m.PayoutPosition, m.Status,
            m.VirtualAccountNumber, m.BankName,
            !string.IsNullOrWhiteSpace(m.PayoutBankAccountNumber),
            Mask(m.PayoutBankAccountNumber),
            m.PayoutBankLabel ?? m.PayoutBankName)).ToList();

        return new MemberAccountsDto(
            circle.Id,
            circle.Name,
            dtos.Count,
            dtos.Count(d => !string.IsNullOrWhiteSpace(d.VirtualAccountNumber)),
            dtos.Count(d => d.HasPayoutAccount),
            dtos);
    }

    private static string? Mask(string? accountNumber) =>
        string.IsNullOrWhiteSpace(accountNumber)
            ? null
            : accountNumber.Length <= 4 ? accountNumber : $"****{accountNumber[^4..]}";
}

public static class ListMemberAccountsEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/circles/{circleId:guid}/member-accounts",
            async (Guid circleId, IMediator mediator, bool includeInactive = false) =>
            {
                var result = await mediator.Send(new ListMemberAccountsQuery(circleId, includeInactive));
                return Results.Ok(ApiResponse<MemberAccountsDto>.Ok(result));
            })
        .WithName("ListCircleMemberAccounts")
        .WithTags("Collections");
}
