using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Features.Members.GetMember;

public record GetMemberQuery(Guid MemberId) : IRequest<MemberDetailDto>;

public record MemberDetailDto(
    Guid Id, Guid CircleId, string Name, string Phone, string? Email,
    int PayoutPosition, string? VirtualAccountNumber, string? BankName,
    MemberStatus Status, int CreditScore, string CreditTier,
    int ConsecutiveOnTimeStreak, DateTime JoinedAt);

public class GetMemberHandler(AppDbContext db) : IRequestHandler<GetMemberQuery, MemberDetailDto>
{
    public async Task<MemberDetailDto> Handle(GetMemberQuery q, CancellationToken ct)
    {
        var m = await db.Members.FirstOrDefaultAsync(x => x.Id == q.MemberId, ct)
            ?? throw new NotFoundException(nameof(Member), q.MemberId);

        return new MemberDetailDto(
            m.Id, m.CircleId, m.Name, m.Phone, m.Email,
            m.PayoutPosition, m.VirtualAccountNumber, m.BankName,
            m.Status, m.CreditScore, m.CreditTier, m.ConsecutiveOnTimeStreak, m.JoinedAt);
    }
}

public static class GetMemberEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/members/{id:guid}", async (Guid id, IMediator mediator) =>
        {
            var result = await mediator.Send(new GetMemberQuery(id));
            return Results.Ok(ApiResponse<MemberDetailDto>.Ok(result));
        })
        .WithName("GetMember")
        .WithTags("Members")
        .AllowAnonymous();
}
