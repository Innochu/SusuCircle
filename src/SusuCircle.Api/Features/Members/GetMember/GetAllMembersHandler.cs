using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Features.Members.GetMember;

// ── Request / Response ────────────────────────────────────────────────────────

public record GetAllMembersQuery(
    Guid AdminId,
    string? Search = null,
    string? CircleId = null,
    string? Status = null,
    int Page = 1,
    int PageSize = 50) : IRequest<PagedResponse<AdminMemberDto>>;

public record AdminMemberDto(
    Guid MemberId,
    string Name,
    string Phone,
    string? VirtualAccountNumber,
    int PayoutPosition,
    string Status,
    decimal PaidAmount,
    decimal ExpectedAmount,
    Guid CircleId,
    string CircleName,
    string CirclePlan,
    int CurrentCycle);

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetAllMembersHandler(AppDbContext db) : IRequestHandler<GetAllMembersQuery, PagedResponse<AdminMemberDto>>
{
    public async Task<PagedResponse<AdminMemberDto>> Handle(GetAllMembersQuery q, CancellationToken ct)
    {
        _ = await db.Admins.FindAsync([q.AdminId], ct)
            ?? throw new NotFoundException(nameof(Admin), q.AdminId);

        // Base query — all members in circles belonging to this admin
        var query = db.Members
            .Include(m => m.Circle)
            .Include(m => m.Contributions)
            .Where(m => m.Circle.AdminId == q.AdminId);

        // Optional filters
        if (!string.IsNullOrWhiteSpace(q.Search))
            query = query.Where(m =>
                m.Name.ToLower().Contains(q.Search.ToLower()) ||
                m.Phone.Contains(q.Search));

        if (!string.IsNullOrWhiteSpace(q.CircleId) && Guid.TryParse(q.CircleId, out var circleGuid))
            query = query.Where(m => m.CircleId == circleGuid);

        if (!string.IsNullOrWhiteSpace(q.Status) && Enum.TryParse<MemberStatus>(q.Status, true, out var statusEnum))
            query = query.Where(m => m.Status == statusEnum);

        var total = await query.CountAsync(ct);

        var members = await query
            .OrderBy(m => m.Circle.Name)
            .ThenBy(m => m.PayoutPosition)
            .Skip((q.Page - 1) * q.PageSize)
            .Take(q.PageSize)
            .ToListAsync(ct);

        var result = members.Select(m =>
        {
            // Get current cycle contribution for this member
            var currentContrib = m.Contributions
                .FirstOrDefault(c => c.CycleNumber == m.Circle.CurrentCycle);

            // Derive payment status label from contribution
            var contributionStatus = currentContrib?.Status switch
            {
                ContributionStatus.Paid     => "Paid",
                ContributionStatus.Overpaid => "Paid",
                ContributionStatus.Partial  => "Partial",
                ContributionStatus.Defaulted => "Defaulted",
                _                           => "Pending"
            };

            return new AdminMemberDto(
                m.Id,
                m.Name,
                m.Phone,
                m.VirtualAccountNumber,
                m.PayoutPosition,
                contributionStatus,
                currentContrib?.PaidAmount ?? 0,
                currentContrib?.ExpectedAmount ?? m.Circle.ContributionAmount,
                m.CircleId,
                m.Circle.Name,
                m.Circle.Plan.ToString(),
                m.Circle.CurrentCycle);
        });

        return new PagedResponse<AdminMemberDto>(result, total, q.Page, q.PageSize);
    }
}

// ── Endpoint ──────────────────────────────────────────────────────────────────

public static class GetAllMembersEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/admin/{adminId:guid}/members",
            async (
                Guid adminId,
                IMediator mediator,
                string? search = null,
                string? circleId = null,
                string? status = null,
                int page = 1,
                int pageSize = 50) =>
            {
                var result = await mediator.Send(
                    new GetAllMembersQuery(adminId, search, circleId, status, page, pageSize));
                return Results.Ok(ApiResponse<PagedResponse<AdminMemberDto>>.Ok(result));
            })
        .WithName("GetAllMembers")
        .WithTags("Admin")
        .AllowAnonymous();
}
