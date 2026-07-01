using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Features.Circles.GetCircleMembers;

// ── Request ───────────────────────────────────────────────────────────────────

// adminId enforces ownership: an admin can only read members of circles they own.
public record GetCircleMembersQuery(Guid AdminId, Guid CircleId) : IRequest<CircleMembersResponse>;

public record CircleMembersResponse(
    Guid CircleId,
    string CircleName,
    string Plan,
    int CurrentCycle,
    decimal ContributionAmount,
    int MemberCount,
    List<CircleMemberDto> Members);

public record CircleMemberDto(
    Guid Id,
    string Name,
    string Phone,
    string? Email,
    string? VirtualAccountNumber,
    int PayoutPosition,
    string MemberStatus,
    int ConsecutiveOnTimeStreak,
    // Current-cycle contribution snapshot
    string ContributionStatus,
    decimal ExpectedAmount,
    decimal PaidAmount,
    decimal Balance,
    DateTime? LastPaymentAt,
    DateTime? DueDate);

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetCircleMembersHandler(AppDbContext db)
    : IRequestHandler<GetCircleMembersQuery, CircleMembersResponse>
{
    public async Task<CircleMembersResponse> Handle(GetCircleMembersQuery q, CancellationToken ct)
    {
        var circle = await db.Circles
            .FirstOrDefaultAsync(c => c.Id == q.CircleId && c.AdminId == q.AdminId, ct)
            ?? throw new NotFoundException(nameof(Circle), q.CircleId);

        var members = await db.Members
            .Where(m => m.CircleId == circle.Id)
            .OrderBy(m => m.PayoutPosition)
            .ToListAsync(ct);

        // Pull current-cycle contributions in one query, then join in memory
        var memberIds = members.Select(m => m.Id).ToList();
        var contributions = await db.Contributions
            .Where(c => c.CircleId == circle.Id
                && c.CycleNumber == circle.CurrentCycle
                && memberIds.Contains(c.MemberId))
            .ToListAsync(ct);

        var contribByMember = contributions.ToDictionary(c => c.MemberId);

        var dtos = members.Select(m =>
        {
            contribByMember.TryGetValue(m.Id, out var contrib);

            var expected = contrib?.ExpectedAmount ?? circle.ContributionAmount;
            var paid = contrib?.PaidAmount ?? 0m;
            var credit = contrib?.CreditApplied ?? 0m;
            var balance = expected - credit - paid;
            var status = contrib?.Status.ToString() ?? "Unpaid";

            return new CircleMemberDto(
                m.Id,
                m.Name,
                m.Phone,
                m.Email,
                m.VirtualAccountNumber,
                m.PayoutPosition,
                m.Status.ToString(),
                m.ConsecutiveOnTimeStreak,
                status,
                expected,
                paid,
                balance,
                contrib?.PaidAt,
                contrib?.DueDate);
        }).ToList();

        return new CircleMembersResponse(
            circle.Id,
            circle.Name,
            circle.Plan.ToString(),
            circle.CurrentCycle,
            circle.ContributionAmount,
            dtos.Count,
            dtos);
    }
}

// ── Endpoint ──────────────────────────────────────────────────────────────────

public static class GetCircleMembersEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/admin/{adminId:guid}/circles/{circleId:guid}/members",
            async (Guid adminId, Guid circleId, IMediator mediator) =>
            {
                var result = await mediator.Send(new GetCircleMembersQuery(adminId, circleId));
                return Results.Ok(ApiResponse<CircleMembersResponse>.Ok(result));
            })
        .WithName("GetCircleMembers")
        .WithTags("Circles")
        .AllowAnonymous();
}
