using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Features.Contributions.GetContributionBoard;

public record GetContributionBoardQuery(Guid CircleId, int? CycleNumber = null) : IRequest<ContributionBoardDto>;

public record ContributionBoardDto(
    Guid CircleId,
    string CircleName,
    int CycleNumber,
    decimal ExpectedPerMember,
    decimal TotalExpected,
    decimal TotalCollected,
    int PaidCount,
    int PendingCount,
    int PartialCount,
    int DefaultedCount,
    bool PayoutReady,
    IEnumerable<MemberContributionDto> Members);

public record MemberContributionDto(
    Guid MemberId,
    string MemberName,
    int PayoutPosition,
    ContributionStatus Status,
    decimal ExpectedAmount,
    decimal PaidAmount,
    decimal Balance,
    decimal CreditApplied,
    DateTime DueDate,
    DateTime? PaidAt);

public class GetContributionBoardHandler(AppDbContext db) : IRequestHandler<GetContributionBoardQuery, ContributionBoardDto>
{
    public async Task<ContributionBoardDto> Handle(GetContributionBoardQuery q, CancellationToken ct)
    {
        var circle = await db.Circles
            .FirstOrDefaultAsync(c => c.Id == q.CircleId, ct)
            ?? throw new NotFoundException(nameof(Circle), q.CircleId);

        var cycle = q.CycleNumber ?? circle.CurrentCycle;

        var contributions = await db.Contributions
            .Include(c => c.Member)
            .Where(c => c.CircleId == q.CircleId && c.CycleNumber == cycle)
            .OrderBy(c => c.Member.PayoutPosition)
            .ToListAsync(ct);

        var activeCount = contributions.Count;
        var paid        = contributions.Count(c => c.Status is ContributionStatus.Paid or ContributionStatus.Overpaid);
        var partial     = contributions.Count(c => c.Status == ContributionStatus.Partial);
        var defaulted   = contributions.Count(c => c.Status == ContributionStatus.Defaulted);
        var pending     = contributions.Count(c => c.Status == ContributionStatus.Pending);
        var collected   = contributions.Sum(c => c.PaidAmount);
        var expected    = circle.ContributionAmount * activeCount;

        bool payoutReady = circle.Plan switch
        {
            PlanType.BAM     => contributions.All(c => c.Status is ContributionStatus.Paid or ContributionStatus.Overpaid),
            PlanType.ADASHI  => collected >= expected,
            _ => false
        };

        var members = contributions.Select(c => new MemberContributionDto(
            c.MemberId, c.Member.Name, c.Member.PayoutPosition,
            c.Status, c.ExpectedAmount, c.PaidAmount, c.Balance,
            c.CreditApplied, c.DueDate, c.PaidAt));

        return new ContributionBoardDto(
            circle.Id, circle.Name, cycle, circle.ContributionAmount,
            expected, collected, paid, pending, partial, defaulted, payoutReady, members);
    }
}

public static class GetContributionBoardEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/circles/{circleId:guid}/board",
            async (Guid circleId, IMediator mediator, int? cycle = null) =>
            {
                var result = await mediator.Send(new GetContributionBoardQuery(circleId, cycle));
                return Results.Ok(ApiResponse<ContributionBoardDto>.Ok(result));
            })
        .WithName("GetContributionBoard")
        .WithTags("Contributions")
        .RequireAuthorization();
}
