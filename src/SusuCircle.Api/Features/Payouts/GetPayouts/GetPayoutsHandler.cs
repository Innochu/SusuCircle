using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Features.Payouts.GetPayouts;

public record GetPayoutsQuery(Guid CircleId) : IRequest<IEnumerable<PayoutHistoryDto>>;

public record PayoutHistoryDto(
    Guid Id, int CycleNumber, string RecipientName, decimal ExpectedAmount,
    decimal DisbursedAmount, PayoutStatus Status, DateTime ScheduledAt, DateTime? DisbursedAt);

public class GetPayoutsHandler(AppDbContext db) : IRequestHandler<GetPayoutsQuery, IEnumerable<PayoutHistoryDto>>
{
    public async Task<IEnumerable<PayoutHistoryDto>> Handle(GetPayoutsQuery q, CancellationToken ct)
    {
        _ = await db.Circles.FindAsync([q.CircleId], ct)
            ?? throw new NotFoundException(nameof(Circle), q.CircleId);

        return await db.Payouts
            .Include(p => p.Member)
            .Where(p => p.CircleId == q.CircleId)
            .OrderByDescending(p => p.CycleNumber)
            .Select(p => new PayoutHistoryDto(
                p.Id, p.CycleNumber, p.Member.Name, p.ExpectedAmount,
                p.DisbursedAmount, p.Status, p.ScheduledAt, p.DisbursedAt))
            .ToListAsync(ct);
    }
}

public static class GetPayoutsEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/circles/{circleId:guid}/payouts",
            async (Guid circleId, IMediator mediator) =>
            {
                var result = await mediator.Send(new GetPayoutsQuery(circleId));
                return Results.Ok(ApiResponse<IEnumerable<PayoutHistoryDto>>.Ok(result));
            })
        .WithName("GetPayouts")
        .WithTags("Payouts")
        .AllowAnonymous();
}
