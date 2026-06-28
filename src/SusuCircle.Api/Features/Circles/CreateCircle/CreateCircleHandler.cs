using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Features.Circles.CreateCircle;

// ── Request / Response ────────────────────────────────────────────────────────

public record CreateCircleCommand(
    Guid AdminId,
    string Name,
    string? Description,
    PlanType Plan,
    decimal ContributionAmount,
    ContributionFrequency Frequency,
    int MaxMembers,
    PayoutOrderType PayoutOrder,
    DateTime StartDate) : IRequest<CircleDto>;

public record CircleDto(
    Guid Id,
    string Name,
    PlanType Plan,
    decimal ContributionAmount,
    ContributionFrequency Frequency,
    int MaxMembers,
    int CurrentMemberCount,
    CircleStatus Status,
    DateTime StartDate,
    DateTime NextContributionDate);

// ── Validator ─────────────────────────────────────────────────────────────────

public class CreateCircleValidator : AbstractValidator<CreateCircleCommand>
{
    public CreateCircleValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.ContributionAmount).GreaterThan(0);
        RuleFor(x => x.MaxMembers)
            .GreaterThanOrEqualTo(2)
            .Must((cmd, max) => cmd.Plan == PlanType.ADASHI ? max <= 50 : max <= 12)
            .WithMessage("BAM supports up to 12 members. ADASHI supports up to 50.");
        RuleFor(x => x.StartDate).GreaterThanOrEqualTo(DateTime.UtcNow.Date);
        RuleFor(x => x.Frequency)
            .Must((cmd, freq) => cmd.Plan == PlanType.BAM ? freq == ContributionFrequency.Monthly : true)
            .WithMessage("BAM plan only supports monthly frequency.");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class CreateCircleHandler(AppDbContext db) : IRequestHandler<CreateCircleCommand, CircleDto>
{
    public async Task<CircleDto> Handle(CreateCircleCommand cmd, CancellationToken ct)
    {
        var admin = await db.Admins.FindAsync([cmd.AdminId], ct)
            ?? throw new NotFoundException(nameof(Admin), cmd.AdminId);

        var nextDate = ComputeNextDate(cmd.StartDate, cmd.Frequency);

        var circle = new Circle
        {
            Id                   = Guid.NewGuid(),
            AdminId              = cmd.AdminId,
            Name                 = cmd.Name,
            Description          = cmd.Description,
            Plan                 = cmd.Plan,
            ContributionAmount   = cmd.ContributionAmount,
            Frequency            = cmd.Frequency,
            MaxMembers           = cmd.MaxMembers,
            PayoutOrder          = cmd.PayoutOrder,
            StartDate            = cmd.StartDate,
            NextContributionDate = nextDate,
            Status               = CircleStatus.Setup,
        };

        db.Circles.Add(circle);
        await db.SaveChangesAsync(ct);

        return MapToDto(circle, 0);
    }

    private static DateTime ComputeNextDate(DateTime start, ContributionFrequency freq) => freq switch
    {
        ContributionFrequency.Weekly    => start.AddDays(7),
        ContributionFrequency.Biweekly  => start.AddDays(14),
        ContributionFrequency.Monthly   => start.AddMonths(1),
        _ => start.AddMonths(1)
    };

    public static CircleDto MapToDto(Circle c, int memberCount) => new(
        c.Id, c.Name, c.Plan, c.ContributionAmount, c.Frequency,
        c.MaxMembers, memberCount, c.Status, c.StartDate, c.NextContributionDate);
}

// ── Endpoint ──────────────────────────────────────────────────────────────────

public static class CreateCircleEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/circles", async (CreateCircleCommand cmd, IMediator mediator) =>
        {
            var result = await mediator.Send(cmd);
            return Results.Created($"/api/circles/{result.Id}", ApiResponse<CircleDto>.Ok(result, "Circle created."));
        })
        .WithName("CreateCircle")
        .WithTags("Circles");
        //.RequireAuthorization();
}
