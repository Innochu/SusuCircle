using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Features.Contributions.GetContributions;

public record GetContributionsQuery(Guid MemberId) : IRequest<IEnumerable<ContributionDto>>;

public record ContributionDto(
    Guid Id, int CycleNumber, decimal ExpectedAmount, decimal PaidAmount,
    decimal Balance, decimal CreditApplied, ContributionStatus Status,
    DateTime DueDate, DateTime? PaidAt);

public class GetContributionsHandler(AppDbContext db) : IRequestHandler<GetContributionsQuery, IEnumerable<ContributionDto>>
{
    public async Task<IEnumerable<ContributionDto>> Handle(GetContributionsQuery q, CancellationToken ct)
    {
        _ = await db.Members.FindAsync([q.MemberId], ct)
            ?? throw new NotFoundException(nameof(Member), q.MemberId);

        return await db.Contributions
            .Where(c => c.MemberId == q.MemberId)
            .OrderByDescending(c => c.CycleNumber)
            .Select(c => new ContributionDto(
                c.Id, c.CycleNumber, c.ExpectedAmount, c.PaidAmount,
                c.ExpectedAmount - c.CreditApplied - c.PaidAmount,
                c.CreditApplied, c.Status, c.DueDate, c.PaidAt))
            .ToListAsync(ct);
    }
}

public static class GetContributionsEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/members/{memberId:guid}/contributions",
            async (Guid memberId, IMediator mediator) =>
            {
                var result = await mediator.Send(new GetContributionsQuery(memberId));
                return Results.Ok(ApiResponse<IEnumerable<ContributionDto>>.Ok(result));
            })
        .WithName("GetContributions")
        .WithTags("Contributions")
        .RequireAuthorization();
}
