using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;
using SusuCircle.Api.Common.Services;

namespace SusuCircle.Api.Features.Circles.UpdateCircleStatus;

public record UpdateCircleStatusCommand(Guid CircleId, CircleStatus NewStatus) : IRequest<bool>;

public class UpdateCircleStatusValidator : AbstractValidator<UpdateCircleStatusCommand>
{
    public UpdateCircleStatusValidator()
    {
        RuleFor(x => x.CircleId).NotEmpty();
        RuleFor(x => x.NewStatus).IsInEnum();
    }
}

public class UpdateCircleStatusHandler(AppDbContext db, INotificationService notifications)
    : IRequestHandler<UpdateCircleStatusCommand, bool>
{
    public async Task<bool> Handle(UpdateCircleStatusCommand cmd, CancellationToken ct)
    {
        var circle = await db.Circles
            .Include(c => c.Members)
            .FirstOrDefaultAsync(c => c.Id == cmd.CircleId, ct)
            ?? throw new NotFoundException(nameof(Circle), cmd.CircleId);

        circle.Status = cmd.NewStatus;
        await db.SaveChangesAsync(ct);

        if (cmd.NewStatus == CircleStatus.Paused)
        {
            var memberIds = circle.Members.Select(m => m.Id);
            await notifications.SendBulkAsync(memberIds, NotificationType.CirclePaused,
                "Circle paused", $"'{circle.Name}' has been temporarily paused by the coordinator.", ct);
        }

        return true;
    }
}

public static class UpdateCircleStatusEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPatch("/api/circles/status", async (UpdateCircleStatusCommand cmd, IMediator mediator) =>
        {
            await mediator.Send(cmd with { CircleId = cmd.CircleId });
            return Results.Ok(ApiResponse<string>.Ok("Status updated."));
        })
        .WithName("UpdateCircleStatus")
        .WithTags("Circles");
        //.RequireAuthorization();
}
