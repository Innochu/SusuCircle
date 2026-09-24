using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Features.Collections.ListCollectionAccounts;

// ══════════════════════════════════════════════════════════════════════════════
// The collection accounts belonging to ONE circle — scoped by circle id, so a
// circle can never be shown the pooled account of another circle.
//
// A circle has exactly one ACTIVE account at a time (enforced by a filtered
// unique index in CollectionAccountConfiguration). More than one row can exist
// because accounts are superseded rather than edited — a provider migration or a
// reissued virtual account creates a new row — and closed cycles still have to
// be explainable against the account that actually held their funds.
// ══════════════════════════════════════════════════════════════════════════════

public record ListCollectionAccountsQuery(Guid CircleId, bool IncludeInactive = false)
    : IRequest<CollectionAccountsDto>;

public record CollectionAccountsDto(
    Guid CircleId,
    string CircleName,
    int CurrentCycle,
    CollectionAccountDto? Active,
    IReadOnlyList<CollectionAccountDto> Accounts);

public record CollectionAccountDto(
    Guid Id,
    string Provider,
    string AccountNumber,
    string AccountName,
    string BankName,
    string BankCode,
    bool IsActive,
    // TRUE when this row is a stand-in created because provisioning failed at
    // circle creation. It cannot receive money. ProvisioningError says why, and
    // POST /api/circles/{id}/collection-account upgrades it in place.
    bool IsPlaceholder,
    string? ProvisioningError,
    decimal Balance,
    decimal TotalCredited,
    decimal TotalDebited,
    int EntryCount,
    DateTime CreatedAt,
    DateTime? DeactivatedAt);

public class ListCollectionAccountsHandler(AppDbContext db)
    : IRequestHandler<ListCollectionAccountsQuery, CollectionAccountsDto>
{
    public async Task<CollectionAccountsDto> Handle(ListCollectionAccountsQuery q, CancellationToken ct)
    {
        var circle = await db.Circles
            .FirstOrDefaultAsync(c => c.Id == q.CircleId, ct)
            ?? throw new NotFoundException(nameof(Circle), q.CircleId);

        var accounts = await db.CollectionAccounts
            .Where(a => a.CircleId == circle.Id && (q.IncludeInactive || a.IsActive))
            .OrderByDescending(a => a.IsActive)
            .ThenByDescending(a => a.CreatedAt)
            .ToListAsync(ct);

        if (accounts.Count == 0)
            return new CollectionAccountsDto(circle.Id, circle.Name, circle.CurrentCycle, null, []);

        // Balances aggregated in one grouped query rather than per account, so
        // listing history does not fan out into a query per row.
        var accountIds = accounts.Select(a => a.Id).ToList();
        var totals = await db.CollectionLedgerEntries
            .Where(e => accountIds.Contains(e.CollectionAccountId))
            .GroupBy(e => new { e.CollectionAccountId, e.Direction })
            .Select(g => new
            {
                g.Key.CollectionAccountId,
                g.Key.Direction,
                Total = g.Sum(e => e.Amount),
                Count = g.Count(),
            })
            .ToListAsync(ct);

        var dtos = accounts.Select(a =>
        {
            var credited = totals
                .Where(t => t.CollectionAccountId == a.Id && t.Direction == CollectionEntryDirection.Credit)
                .Sum(t => t.Total);
            var debited = totals
                .Where(t => t.CollectionAccountId == a.Id && t.Direction == CollectionEntryDirection.Debit)
                .Sum(t => t.Total);
            var entries = totals.Where(t => t.CollectionAccountId == a.Id).Sum(t => t.Count);

            return new CollectionAccountDto(
                a.Id, a.Provider, a.AccountNumber, a.AccountName, a.BankName, a.BankCode,
                a.IsActive, a.IsPlaceholder, a.ProvisioningError,
                credited - debited, credited, debited, entries,
                a.CreatedAt, a.DeactivatedAt);
        }).ToList();

        return new CollectionAccountsDto(
            circle.Id, circle.Name, circle.CurrentCycle,
            dtos.FirstOrDefault(d => d.IsActive),
            dtos);
    }
}

public static class ListCollectionAccountsEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/circles/{circleId:guid}/collection-accounts",
            async (Guid circleId, IMediator mediator, bool includeInactive = false) =>
            {
                var result = await mediator.Send(new ListCollectionAccountsQuery(circleId, includeInactive));
                return Results.Ok(ApiResponse<CollectionAccountsDto>.Ok(result));
            })
        .WithName("ListCollectionAccounts")
        .WithTags("Collections");
}
