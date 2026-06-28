using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Features.Members.GetMember;

// ── Request / Response ────────────────────────────────────────────────────────

public record GetOverviewQuery(Guid AdminId) : IRequest<OverviewDto>;

public record OverviewDto(
    string AdminName,
    int ActiveCirclesCount,
    int TotalMembers,
    decimal TotalCollected,
    int CollectionRatePercent,
    IEnumerable<TrendPointDto> ContributionTrends,
    IEnumerable<ActiveCircleDto> ActiveCircles);

public record TrendPointDto(string Month, decimal Expected, decimal Actual);

public record ActiveCircleDto(
    Guid Id,
    string Name,
    string Plan,
    string CycleInfo,
    int MemberCount,
    int MaxMembers,
    decimal ContributionAmount,
    string Frequency,
    int CollectionRatePercent,
    string Status);

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetOverviewHandler(AppDbContext db) : IRequestHandler<GetOverviewQuery, OverviewDto>
{
    public async Task<OverviewDto> Handle(GetOverviewQuery q, CancellationToken ct)
    {
        var admin = await db.Admins.FindAsync([q.AdminId], ct)
            ?? throw new NotFoundException(nameof(Admin), q.AdminId);

        var circles = await db.Circles
            .Include(c => c.Members)
            .Include(c => c.Contributions)
            .Where(c => c.AdminId == q.AdminId)
            .ToListAsync(ct);

        var activeCircles  = circles.Where(c => c.Status == CircleStatus.Active).ToList();
        var totalMembers   = activeCircles.Sum(c => c.Members.Count(m => m.Status == MemberStatus.Active));
        var totalCollected = circles.SelectMany(c => c.Contributions).Sum(c => c.PaidAmount);

        // Collection rate this cycle across all circles
        var currentContribs = circles
            .SelectMany(c => c.Contributions.Where(x => x.CycleNumber == c.CurrentCycle))
            .ToList();

        var paidCount     = currentContribs.Count(c => c.Status is ContributionStatus.Paid or ContributionStatus.Overpaid);
        var totalCount    = currentContribs.Count;
        var collectionRate = totalCount > 0 ? (int)Math.Round((double)paidCount / totalCount * 100) : 0;

        // Trend chart — last 7 months expected vs actual
        var trends = await BuildTrendsAsync(q.AdminId, ct);

        // Active circles list for the sidebar widget
        var circleList = activeCircles.Select(c =>
        {
            var cycleContribs = c.Contributions.Where(x => x.CycleNumber == c.CurrentCycle).ToList();
            var paid          = cycleContribs.Sum(x => x.PaidAmount);
            var expected      = cycleContribs.Sum(x => x.ExpectedAmount);
            var rate          = expected > 0 ? (int)Math.Round(paid / expected * 100) : 0;
            var activeMembers = c.Members.Count(m => m.Status == MemberStatus.Active);

            return new ActiveCircleDto(
                c.Id,
                c.Name,
                c.Plan.ToString(),
                $"Cycle {c.CurrentCycle}/{c.MaxMembers} · {activeMembers}/{c.MaxMembers} members · ₦{c.ContributionAmount:N0}/{c.Frequency.ToString().ToLower()}",
                activeMembers,
                c.MaxMembers,
                c.ContributionAmount,
                c.Frequency.ToString(),
                rate,
                c.Status.ToString());
        });

        return new OverviewDto(
            admin.Name,
            activeCircles.Count,
            totalMembers,
            totalCollected,
            collectionRate,
            trends,
            circleList);
    }

    private async Task<IEnumerable<TrendPointDto>> BuildTrendsAsync(Guid adminId, CancellationToken ct)
    {
        var now    = DateTime.UtcNow;
        var trends = new List<TrendPointDto>();

        for (int i = 6; i >= 0; i--)
        {
            var month      = now.AddMonths(-i);
            var monthStart = new DateTime(month.Year, month.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var monthEnd   = monthStart.AddMonths(1);

            var contribs = await db.Contributions
                .Include(c => c.Circle)
                .Where(c =>
                    c.Circle.AdminId == adminId &&
                    c.DueDate >= monthStart &&
                    c.DueDate < monthEnd)
                .ToListAsync(ct);

            trends.Add(new TrendPointDto(
                month.ToString("MMM"),
                contribs.Sum(c => c.ExpectedAmount),
                contribs.Sum(c => c.PaidAmount)));
        }

        return trends;
    }
}

// ── Endpoint ──────────────────────────────────────────────────────────────────

public static class GetOverviewEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/admin/{adminId:guid}/overview",
            async (Guid adminId, IMediator mediator) =>
            {
                var result = await mediator.Send(new GetOverviewQuery(adminId));
                return Results.Ok(ApiResponse<OverviewDto>.Ok(result));
            })
        .WithName("GetOverview")
        .WithTags("Admin")
        .AllowAnonymous();
}
