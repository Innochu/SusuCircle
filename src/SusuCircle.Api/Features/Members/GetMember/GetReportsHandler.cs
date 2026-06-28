using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Features.Members.GetMember;

// ── Request / Response ────────────────────────────────────────────────────────

public record GetReportsQuery(Guid AdminId, int? Year = null) : IRequest<ReportsDto>;

public record ReportsDto(
    decimal TotalCollected,
    decimal TotalExpected,
    int OverallRatePercent,
    int ActiveCirclesCount,
    IEnumerable<MonthlyCollectionDto> MonthlyCollections,
    IEnumerable<CircleBreakdownDto> CircleBreakdowns);

public record MonthlyCollectionDto(string Month, int MonthNumber, decimal Collected, decimal Expected);

public record CircleBreakdownDto(
    Guid CircleId,
    string Name,
    string Plan,
    string CycleInfo,
    decimal Collected,
    decimal Expected,
    int RatePercent);

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetReportsHandler(AppDbContext db) : IRequestHandler<GetReportsQuery, ReportsDto>
{
    public async Task<ReportsDto> Handle(GetReportsQuery q, CancellationToken ct)
    {
        _ = await db.Admins.FindAsync([q.AdminId], ct)
            ?? throw new NotFoundException(nameof(Admin), q.AdminId);

        var year = q.Year ?? DateTime.UtcNow.Year;

        var circles = await db.Circles
            .Include(c => c.Members)
            .Include(c => c.Contributions)
            .Where(c => c.AdminId == q.AdminId)
            .ToListAsync(ct);

        var allContribs = circles.SelectMany(c => c.Contributions).ToList();

        var totalCollected = allContribs.Sum(c => c.PaidAmount);
        var totalExpected  = allContribs.Sum(c => c.ExpectedAmount);
        var overallRate    = totalExpected > 0
            ? (int)Math.Round(totalCollected / totalExpected * 100)
            : 0;
        var activeCount = circles.Count(c => c.Status == CircleStatus.Active);

        // Monthly collections for the bar chart (Jan–Dec of selected year)
        var monthlyCollections = Enumerable.Range(1, 12).Select(month =>
        {
            var monthContribs = allContribs
                .Where(c => c.DueDate.Year == year && c.DueDate.Month == month)
                .ToList();

            return new MonthlyCollectionDto(
                new DateTime(year, month, 1).ToString("MMM"),
                month,
                monthContribs.Sum(c => c.PaidAmount),
                monthContribs.Sum(c => c.ExpectedAmount));
        });

        // Per-circle breakdown cards
        var circleBreakdowns = circles.Select(c =>
        {
            var cycleContribs = c.Contributions
                .Where(x => x.CycleNumber == c.CurrentCycle)
                .ToList();

            var collected = cycleContribs.Sum(x => x.PaidAmount);
            var expected  = cycleContribs.Sum(x => x.ExpectedAmount);
            var rate      = expected > 0 ? (int)Math.Round(collected / expected * 100) : 0;
            var members   = c.Members.Count(m => m.Status == MemberStatus.Active);

            return new CircleBreakdownDto(
                c.Id,
                c.Name,
                c.Plan.ToString(),
                $"Cycle {c.CurrentCycle} of {c.MaxMembers} · {c.Frequency}",
                collected,
                expected,
                rate);
        });

        return new ReportsDto(
            totalCollected,
            totalExpected,
            overallRate,
            activeCount,
            monthlyCollections,
            circleBreakdowns);
    }
}

// ── Endpoint ──────────────────────────────────────────────────────────────────

public static class GetReportsEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/admin/{adminId:guid}/reports",
            async (Guid adminId, IMediator mediator, int? year = null) =>
            {
                var result = await mediator.Send(new GetReportsQuery(adminId, year));
                return Results.Ok(ApiResponse<ReportsDto>.Ok(result));
            })
        .WithName("GetReports")
        .WithTags("Admin")
        .AllowAnonymous();
}
