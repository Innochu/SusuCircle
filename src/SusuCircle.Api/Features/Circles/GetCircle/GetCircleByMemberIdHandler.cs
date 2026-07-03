using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Features.Circles.GetCircle;

// ── Request / Response ────────────────────────────────────────────────────────

public record GetCircleByMemberIdQuery(Guid MemberId) : IRequest<CircleByMemberDto>;

public record CircleByMemberDto(
    Guid CircleId,
    string CircleName,
    string? Description,
    string Plan,
    decimal ContributionAmount,
    string Frequency,
    int MaxMembers,
    int CurrentCycle,
    string Status,
    string PayoutOrder,
    DateTime StartDate,
    DateTime NextContributionDate,
    int MemberCount,
    Guid AdminId,
    // Echo back the requesting member's own position/context — convenient for
    // a frontend that only has a memberId and needs "my circle + my spot in it."
    int RequestingMemberPayoutPosition,
    string RequestingMemberStatus);

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetCircleByMemberIdHandler(AppDbContext db)
    : IRequestHandler<GetCircleByMemberIdQuery, CircleByMemberDto>
{
    public async Task<CircleByMemberDto> Handle(GetCircleByMemberIdQuery q, CancellationToken ct)
    {
        var member = await db.Members
            .Include(m => m.Circle)
            .FirstOrDefaultAsync(m => m.Id == q.MemberId, ct)
            ?? throw new NotFoundException(nameof(Member), q.MemberId);

        var circle = member.Circle;

        var memberCount = await db.Members
            .CountAsync(m => m.CircleId == circle.Id && m.Status == MemberStatus.Active, ct);

        return new CircleByMemberDto(
            circle.Id,
            circle.Name,
            circle.Description,
            circle.Plan.ToString(),
            circle.ContributionAmount,
            circle.Frequency.ToString(),
            circle.MaxMembers,
            circle.CurrentCycle,
            circle.Status.ToString(),
            circle.PayoutOrder.ToString(),
            circle.StartDate,
            circle.NextContributionDate,
            memberCount,
            circle.AdminId,
            member.PayoutPosition,
            member.Status.ToString());
    }
}

// ── Endpoint ──────────────────────────────────────────────────────────────────

public static class GetCircleByMemberIdEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/members/{memberId:guid}/circle",
            async (Guid memberId, IMediator mediator) =>
            {
                var result = await mediator.Send(new GetCircleByMemberIdQuery(memberId));
                return Results.Ok(ApiResponse<CircleByMemberDto>.Ok(result));
            })
        .WithName("GetCircleByMemberId")
        .WithTags("Circles")
        .AllowAnonymous();
}