using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Features.Circles.ListCircles;

public record ListCirclesQuery(Guid AdminId, int Page = 1, int PageSize = 20) : IRequest<PagedResponse<CircleSummaryDto>>;

public record CircleSummaryDto(Guid Id, string Name, PlanType Plan, decimal ContributionAmount, int MemberCount, CircleStatus Status, int CurrentCycle);

public class ListCirclesHandler(AppDbContext db) : IRequestHandler<ListCirclesQuery, PagedResponse<CircleSummaryDto>>
{
    public async Task<PagedResponse<CircleSummaryDto>> Handle(ListCirclesQuery q, CancellationToken ct)
    {
        var query = db.Circles
            .Where(c => c.AdminId == q.AdminId)
            .Include(c => c.Members);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip((q.Page - 1) * q.PageSize)
            .Take(q.PageSize)
            .Select(c => new CircleSummaryDto(
                c.Id, c.Name, c.Plan, c.ContributionAmount,
                c.Members.Count(m => m.Status == MemberStatus.Active),
                c.Status, c.CurrentCycle))
            .ToListAsync(ct);

        return new PagedResponse<CircleSummaryDto>(items, total, q.Page, q.PageSize);
    }
}

public static class ListCirclesEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/circles", async (Guid adminId, IMediator mediator, int page = 1, int pageSize = 20) =>
        {
            var result = await mediator.Send(new ListCirclesQuery(adminId, page, pageSize));
            return Results.Ok(ApiResponse<PagedResponse<CircleSummaryDto>>.Ok(result));
        })
        .WithName("ListCircles")
        .WithTags("Circles");
       // .RequireAuthorization();
}
