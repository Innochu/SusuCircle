using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Features.Members.ListMembers;

public record ListMembersQuery(Guid CircleId) : IRequest<IEnumerable<MemberSummaryDto>>;

public record MemberSummaryDto(
    Guid Id, string Name, string Phone, int PayoutPosition,
    string? VirtualAccountNumber, string? BankName, MemberStatus Status,
    int CreditScore, string CreditTier);

public class ListMembersHandler(AppDbContext db) : IRequestHandler<ListMembersQuery, IEnumerable<MemberSummaryDto>>
{
    public async Task<IEnumerable<MemberSummaryDto>> Handle(ListMembersQuery q, CancellationToken ct)
    {
        _ = await db.Circles.FindAsync([q.CircleId], ct)
            ?? throw new NotFoundException(nameof(Circle), q.CircleId);

        return await db.Members
            .Where(m => m.CircleId == q.CircleId)
            .OrderBy(m => m.PayoutPosition)
            .Select(m => new MemberSummaryDto(
                m.Id, m.Name, m.Phone, m.PayoutPosition,
                m.VirtualAccountNumber, m.BankName, m.Status,
                m.CreditScore, m.CreditTier))
            .ToListAsync(ct);
    }
}

public static class ListMembersEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/circles/{circleId:guid}/members",
            async (Guid circleId, IMediator mediator) =>
            {
                var result = await mediator.Send(new ListMembersQuery(circleId));
                return Results.Ok(ApiResponse<IEnumerable<MemberSummaryDto>>.Ok(result));
            })
        .WithName("ListMembers")
        .WithTags("Members")
        .AllowAnonymous();
}
