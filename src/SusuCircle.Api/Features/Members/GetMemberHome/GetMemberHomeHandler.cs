using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Features.Members.GetMemberHome;

// ── Request / Response ────────────────────────────────────────────────────────
// Powers the member portal Home tab: the VA card, contribution amount, payout
// position, circle summary, and recent activity feed.

public record GetMemberHomeQuery(Guid MemberId) : IRequest<MemberHomeDto>;

public record MemberHomeDto(
    Guid MemberId,
    string MemberName,

    // Contribution account card
    string? VirtualAccountNumber,
    string? BankName,

    // Stat tiles
    decimal ContributionAmount,
    string Frequency,
    int PayoutPosition,
    string PayoutPositionOrdinal,     // "1st", "2nd", "3rd"... for display
    bool IsNextToReceive,
    int ActiveMemberCount,
    int MaxMembers,

    // Circle summary card
    Guid CircleId,
    string CircleName,
    string? CircleDescription,
    string Plan,
    string CircleStatus,
    int CurrentCycle,
    string PayoutOrder,

    // Recent activity feed
    List<MemberActivityDto> RecentActivity);

public record MemberActivityDto(
    Guid Id,
    string Title,
    string Body,
    bool IsRead,
    DateTime CreatedAt);

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetMemberHomeHandler(AppDbContext db) : IRequestHandler<GetMemberHomeQuery, MemberHomeDto>
{
    public async Task<MemberHomeDto> Handle(GetMemberHomeQuery q, CancellationToken ct)
    {
        var member = await db.Members
            .Include(m => m.Circle)
            .FirstOrDefaultAsync(m => m.Id == q.MemberId, ct)
            ?? throw new NotFoundException(nameof(Member), q.MemberId);

        var circle = member.Circle;

        var activeMemberCount = await db.Members
            .CountAsync(m => m.CircleId == circle.Id && m.Status == MemberStatus.Active, ct);

        var isNextToReceive = member.PayoutPosition == circle.CurrentCycle;

        var recentActivity = await db.Notifications
            .Where(n => n.MemberId == member.Id)
            .OrderByDescending(n => n.CreatedAt)
            .Take(5)
            .Select(n => new MemberActivityDto(n.Id, n.Title, n.Body, n.IsRead, n.CreatedAt))
            .ToListAsync(ct);

        return new MemberHomeDto(
            member.Id,
            member.Name,
            member.VirtualAccountNumber,
            member.BankName,
            circle.ContributionAmount,
            circle.Frequency.ToString(),
            member.PayoutPosition,
            Ordinal(member.PayoutPosition),
            isNextToReceive,
            activeMemberCount,
            circle.MaxMembers,
            circle.Id,
            circle.Name,
            circle.Description,
            circle.Plan.ToString(),
            circle.Status.ToString(),
            circle.CurrentCycle,
            circle.PayoutOrder.ToString(),
            recentActivity);
    }

    // 1 -> "1st", 2 -> "2nd", 3 -> "3rd", 4 -> "4th", 11-13 -> "th" special case
    private static string Ordinal(int n)
    {
        if (n <= 0) return n.ToString();
        if (n % 100 is >= 11 and <= 13) return $"{n}th";
        return (n % 10) switch
        {
            1 => $"{n}st",
            2 => $"{n}nd",
            3 => $"{n}rd",
            _ => $"{n}th",
        };
    }
}

// ── Endpoint ──────────────────────────────────────────────────────────────────

public static class GetMemberHomeEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/members/{memberId:guid}/home",
            async (Guid memberId, IMediator mediator) =>
            {
                var result = await mediator.Send(new GetMemberHomeQuery(memberId));
                return Results.Ok(ApiResponse<MemberHomeDto>.Ok(result));
            })
        .WithName("GetMemberHome")
        .WithTags("Members")
        .AllowAnonymous();
}