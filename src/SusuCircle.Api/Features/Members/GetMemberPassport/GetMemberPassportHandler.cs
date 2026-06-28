using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Features.Members.GetMemberPassport;

// ADASHI plan only

public record GetMemberPassportQuery(Guid MemberId) : IRequest<MemberPassportDto>;

public record MemberPassportDto(
    Guid MemberId,
    string MemberName,
    int CreditScore,
    string CreditTier,
    int ConsecutiveStreak,
    double OnTimeRatePercent,
    double CompletionRatePercent,
    decimal TotalContributed,
    int CirclesCompleted,
    IEnumerable<CycleRecordDto> CycleHistory);

public record CycleRecordDto(
    int CycleNumber,
    decimal ExpectedAmount,
    decimal PaidAmount,
    decimal Balance,
    ContributionStatus Status,
    DateTime DueDate,
    DateTime? PaidAt,
    bool PaidOnTime);

public class GetMemberPassportHandler(AppDbContext db) : IRequestHandler<GetMemberPassportQuery, MemberPassportDto>
{
    public async Task<MemberPassportDto> Handle(GetMemberPassportQuery q, CancellationToken ct)
    {
        var member = await db.Members
            .Include(m => m.Circle)
            .FirstOrDefaultAsync(m => m.Id == q.MemberId, ct)
            ?? throw new NotFoundException(nameof(Member), q.MemberId);

        if (member.Circle.Plan != PlanType.ADASHI)
            throw new ConflictException("Contribution passport is only available on the ADASHI plan.");

        var contributions = await db.Contributions
            .Where(c => c.MemberId == q.MemberId)
            .OrderBy(c => c.CycleNumber)
            .ToListAsync(ct);

        var total       = contributions.Count;
        var paid        = contributions.Count(c => c.Status is ContributionStatus.Paid or ContributionStatus.Overpaid);
        var onTime      = contributions.Count(c => c.PaidAt.HasValue && c.PaidAt <= c.DueDate);
        var contributed = contributions.Sum(c => c.PaidAmount);

        var history = contributions.Select(c => new CycleRecordDto(
            c.CycleNumber, c.ExpectedAmount, c.PaidAmount, c.Balance, c.Status,
            c.DueDate, c.PaidAt,
            c.PaidAt.HasValue && c.PaidAt <= c.DueDate));

        return new MemberPassportDto(
            member.Id,
            member.Name,
            member.CreditScore,
            member.CreditTier,
            member.ConsecutiveOnTimeStreak,
            total > 0 ? Math.Round((double)onTime / total * 100, 1) : 0,
            total > 0 ? Math.Round((double)paid / total * 100, 1) : 0,
            contributed,
            paid,
            history);
    }
}

public static class GetMemberPassportEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/members/{id:guid}/passport", async (Guid id, IMediator mediator) =>
        {
            var result = await mediator.Send(new GetMemberPassportQuery(id));
            return Results.Ok(ApiResponse<MemberPassportDto>.Ok(result));
        })
        .WithName("GetMemberPassport")
        .WithTags("Members")
        .AllowAnonymous();
}
