using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Features.Circles.GetCircle;

public record GetCircleQuery(Guid CircleId) : IRequest<CircleDetailDto>;

public record CircleDetailDto(
    Guid Id, string Name, string? Description, PlanType Plan,
    decimal ContributionAmount, ContributionFrequency Frequency,
    int MaxMembers, int CurrentMemberCount, int CurrentCycle,
    CircleStatus Status, PayoutOrderType PayoutOrder,
    DateTime StartDate, DateTime NextContributionDate,
    string AdminName);

public class GetCircleHandler(AppDbContext db) : IRequestHandler<GetCircleQuery, CircleDetailDto>
{
    public async Task<CircleDetailDto> Handle(GetCircleQuery q, CancellationToken ct)
    {
        var circle = await db.Circles
            .Include(c => c.Admin)
            .Include(c => c.Members)
            .FirstOrDefaultAsync(c => c.Id == q.CircleId, ct)
            ?? throw new NotFoundException(nameof(Circle), q.CircleId);

        return new CircleDetailDto(
            circle.Id, circle.Name, circle.Description, circle.Plan,
            circle.ContributionAmount, circle.Frequency,
            circle.MaxMembers, circle.Members.Count(m => m.Status == MemberStatus.Active),
            circle.CurrentCycle, circle.Status, circle.PayoutOrder,
            circle.StartDate, circle.NextContributionDate, circle.Admin.Name);
    }
}

public static class GetCircleEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/circles/{id:guid}", async (Guid id, IMediator mediator) =>
        {
            var result = await mediator.Send(new GetCircleQuery(id));
            return Results.Ok(ApiResponse<CircleDetailDto>.Ok(result));
        })
        .WithName("GetCircle")
        .WithTags("Circles");
        //.RequireAuthorization();
}
