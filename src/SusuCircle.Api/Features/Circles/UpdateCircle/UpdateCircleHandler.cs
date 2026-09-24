using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;
using SusuCircle.Api.Common.Services;

namespace SusuCircle.Api.Features.Circles.UpdateCircle;

// ══════════════════════════════════════════════════════════════════════════════
// Edits a circle's terms. PARTIAL update: every field is optional, and a null
// field means "leave this alone" — so a client can send just { name } without
// accidentally blanking the description.
//
// Two fields are deliberately NOT editable here:
//   • Status — already owned by UpdateCircleStatus, which also fires the
//     members' "circle paused" notification. Duplicating it here would mean two
//     code paths that can pause a circle, only one of which tells anybody.
//   • Plan   — BAM and ADASHI assign payout positions differently (BAM pins
//     everyone to 0, ADASHI increments sequentially, see AddMemberHandler), so
//     flipping the plan on a circle that already has members would leave the
//     existing positions meaningless with no safe way to recompute them.
//
// The rest are guarded by how far the circle has progressed: once real money
// has moved, terms that would retroactively change what people owe or what
// they're owed are frozen rather than silently rewritten.
// ══════════════════════════════════════════════════════════════════════════════

// ── Request / Response ────────────────────────────────────────────────────────

public record UpdateCircleCommand(
    Guid CircleId,
    string? Name = null,
    string? Description = null,
    decimal? ContributionAmount = null,
    ContributionFrequency? Frequency = null,
    int? MaxMembers = null,
    PayoutOrderType? PayoutOrder = null,
    DateTime? StartDate = null) : IRequest<UpdatedCircleDto>;

public record UpdatedCircleDto(
    Guid Id,
    string Name,
    string? Description,
    PlanType Plan,
    decimal ContributionAmount,
    ContributionFrequency Frequency,
    int MaxMembers,
    int CurrentMemberCount,
    PayoutOrderType PayoutOrder,
    CircleStatus Status,
    int CurrentCycle,
    DateTime StartDate,
    DateTime NextContributionDate,
    IReadOnlyList<string> UpdatedFields);

// ── Validator ─────────────────────────────────────────────────────────────────
// Shape-only checks. Anything that needs to know what's already in the database
// (member counts, whether money has landed) lives in the handler.

public class UpdateCircleValidator : AbstractValidator<UpdateCircleCommand>
{
    public UpdateCircleValidator()
    {
        RuleFor(x => x.CircleId).NotEmpty();

        RuleFor(x => x.Name)
            .NotEmpty().MaximumLength(150)
            .When(x => x.Name is not null);

        RuleFor(x => x.Description)
            .MaximumLength(1000)
            .When(x => x.Description is not null);

        RuleFor(x => x.ContributionAmount)
            .GreaterThan(0)
            .When(x => x.ContributionAmount is not null);

        RuleFor(x => x.MaxMembers)
            .GreaterThanOrEqualTo(2)
            .When(x => x.MaxMembers is not null);

        RuleFor(x => x.Frequency).IsInEnum().When(x => x.Frequency is not null);
        RuleFor(x => x.PayoutOrder).IsInEnum().When(x => x.PayoutOrder is not null);

        RuleFor(x => x.StartDate)
            .GreaterThanOrEqualTo(DateTime.UtcNow.Date)
            .When(x => x.StartDate is not null)
            .WithMessage("Start date cannot be moved into the past.");

        RuleFor(x => x)
            .Must(HasAtLeastOneChange)
            .WithMessage("Provide at least one field to update.");
    }

    private static bool HasAtLeastOneChange(UpdateCircleCommand c) =>
        c.Name is not null || c.Description is not null || c.ContributionAmount is not null
        || c.Frequency is not null || c.MaxMembers is not null || c.PayoutOrder is not null
        || c.StartDate is not null;
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class UpdateCircleHandler(
    AppDbContext db,
    INotificationService notifications,
    ILogger<UpdateCircleHandler> logger)
    : IRequestHandler<UpdateCircleCommand, UpdatedCircleDto>
{
    public async Task<UpdatedCircleDto> Handle(UpdateCircleCommand cmd, CancellationToken ct)
    {
        var circle = await db.Circles
            .Include(c => c.Members)
            .FirstOrDefaultAsync(c => c.Id == cmd.CircleId, ct)
            ?? throw new NotFoundException(nameof(Circle), cmd.CircleId);

        if (circle.Status == CircleStatus.Completed)
            throw new ConflictException($"'{circle.Name}' has completed — its terms can no longer be changed.");

        var activeMembers = circle.Members.Count(m => m.Status == MemberStatus.Active);

        // How far along is this circle? These two facts gate everything below.
        // "Collected" is deliberately PaidAmount > 0 rather than a status check:
        // a Partial contribution is still real money someone has already sent.
        var hasCollectedFunds = await db.Contributions
            .AnyAsync(c => c.CircleId == circle.Id && c.PaidAmount > 0, ct);

        var hasPayouts = await db.Payouts
            .AnyAsync(p => p.CircleId == circle.Id, ct);

        var changed = new List<string>();

        // ── Name / Description — always safe, they carry no financial meaning ──
        if (cmd.Name is not null && cmd.Name != circle.Name)
        {
            circle.Name = cmd.Name;
            changed.Add(nameof(circle.Name));
        }

        if (cmd.Description is not null && cmd.Description != circle.Description)
        {
            circle.Description = cmd.Description;
            changed.Add(nameof(circle.Description));
        }

        // ── MaxMembers ────────────────────────────────────────────────────────
        // Raising the cap is always fine. Lowering it below the people already in
        // the circle is not — that would leave the circle permanently over its own
        // limit and make AddMember's capacity check lie about why it's rejecting.
        if (cmd.MaxMembers is int newMax && newMax != circle.MaxMembers)
        {
            var planCap = circle.Plan == PlanType.ADASHI ? 50 : 12;
            if (newMax > planCap)
                throw new ConflictException($"{circle.Plan} supports at most {planCap} members.");

            if (newMax < activeMembers)
                throw new ConflictException(
                    $"Cannot reduce the limit to {newMax} — '{circle.Name}' already has {activeMembers} active members.");

            circle.MaxMembers = newMax;
            changed.Add(nameof(circle.MaxMembers));
        }

        // ── ContributionAmount ────────────────────────────────────────────────
        // Frozen the moment anyone has paid into the CURRENT cycle. Changing the
        // target mid-collection would silently reclassify what people already sent
        // — a full payment becoming Partial, or Paid becoming Overpaid — and the
        // reconciliation pipeline would then act on numbers nobody agreed to.
        // Earlier cycles are untouched either way; they're already settled.
        if (cmd.ContributionAmount is decimal newAmount && newAmount != circle.ContributionAmount)
        {
            var currentCycleContributions = await db.Contributions
                .Where(c => c.CircleId == circle.Id && c.CycleNumber == circle.CurrentCycle)
                .ToListAsync(ct);

            if (currentCycleContributions.Any(c => c.PaidAmount > 0))
                throw new ConflictException(
                    $"Cannot change the contribution amount — payments have already been received for cycle {circle.CurrentCycle}. " +
                    "Wait for this cycle to close before repricing the circle.");

            circle.ContributionAmount = newAmount;

            // Keep the open cycle's expectations in step with the new terms,
            // otherwise members would keep being chased for the old figure.
            foreach (var contribution in currentCycleContributions)
                contribution.ExpectedAmount = newAmount;

            changed.Add(nameof(circle.ContributionAmount));
        }

        // ── PayoutOrder ───────────────────────────────────────────────────────
        // Frozen once anyone has been paid: the people already paid out were
        // chosen by the old ordering, and re-ordering behind them would hand
        // someone a second turn while skipping someone else entirely.
        if (cmd.PayoutOrder is PayoutOrderType newOrder && newOrder != circle.PayoutOrder)
        {
            if (hasPayouts)
                throw new ConflictException(
                    "Cannot change the payout order — payouts have already been made on this circle.");

            circle.PayoutOrder = newOrder;
            changed.Add(nameof(circle.PayoutOrder));
        }

        // ── Frequency ─────────────────────────────────────────────────────────
        // BAM is monthly by definition (mirrors CreateCircleValidator). Frequency
        // also sets the collection calendar, so it's frozen once money is in.
        if (cmd.Frequency is ContributionFrequency newFrequency && newFrequency != circle.Frequency)
        {
            if (circle.Plan == PlanType.BAM && newFrequency != ContributionFrequency.Monthly)
                throw new ConflictException("BAM circles only support monthly contributions.");

            if (hasCollectedFunds)
                throw new ConflictException(
                    "Cannot change the contribution frequency — contributions have already been collected on this circle.");

            circle.Frequency = newFrequency;
            changed.Add(nameof(circle.Frequency));
        }

        // ── StartDate ─────────────────────────────────────────────────────────
        // Same reasoning: it anchors the whole schedule, so it can only move
        // while the circle hasn't taken any money yet.
        if (cmd.StartDate is DateTime newStart && newStart != circle.StartDate)
        {
            if (hasCollectedFunds)
                throw new ConflictException(
                    "Cannot change the start date — contributions have already been collected on this circle.");

            circle.StartDate = newStart;
            changed.Add(nameof(circle.StartDate));
        }

        // Either of those two moves the next collection date, so recompute it
        // from the (possibly new) anchor rather than leaving a stale date behind.
        if (changed.Contains(nameof(circle.StartDate)) || changed.Contains(nameof(circle.Frequency)))
        {
            circle.NextContributionDate = ComputeNextDate(circle.StartDate, circle.Frequency);

            var openCycle = await db.Contributions
                .Where(c => c.CircleId == circle.Id && c.CycleNumber == circle.CurrentCycle)
                .ToListAsync(ct);

            foreach (var contribution in openCycle)
                contribution.DueDate = circle.NextContributionDate;

            changed.Add(nameof(circle.NextContributionDate));
        }

        if (changed.Count == 0)
        {
            // Every supplied value already matched what's stored. Nothing to
            // write, and nothing worth waking members up for.
            return MapToDto(circle, activeMembers, changed);
        }

        await db.SaveChangesAsync(ct);

        // ── Tell members, but only about terms that affect them ───────────────
        // A coordinator fixing a typo in the description shouldn't ping everyone.
        var memberFacingChanges = changed
            .Where(f => f is nameof(circle.ContributionAmount)
                          or nameof(circle.Frequency)
                          or nameof(circle.PayoutOrder)
                          or nameof(circle.NextContributionDate))
            .ToList();

        if (memberFacingChanges.Count > 0 && activeMembers > 0)
        {
            var memberIds = circle.Members
                .Where(m => m.Status == MemberStatus.Active)
                .Select(m => m.Id);

            await notifications.SendBulkAsync(memberIds, NotificationType.MemberAdded,
                "Circle terms updated",
                $"'{circle.Name}' has been updated by the coordinator. " +
                $"Contribution is now ₦{circle.ContributionAmount:N0} ({circle.Frequency}), " +
                $"next due {circle.NextContributionDate:dd MMM yyyy}.", ct);
        }

        logger.LogInformation("Circle {CircleId} updated — fields: {Fields}",
            circle.Id, string.Join(", ", changed));

        return MapToDto(circle, activeMembers, changed);
    }

    // Mirrors CreateCircleHandler's schedule maths so an edited circle lands on
    // the same calendar a freshly created one would.
    private static DateTime ComputeNextDate(DateTime start, ContributionFrequency freq) => freq switch
    {
        ContributionFrequency.Weekly   => start.AddDays(7),
        ContributionFrequency.Biweekly => start.AddDays(14),
        ContributionFrequency.Monthly  => start.AddMonths(1),
        _ => start.AddMonths(1)
    };

    private static UpdatedCircleDto MapToDto(Circle c, int memberCount, IReadOnlyList<string> changed) => new(
        c.Id, c.Name, c.Description, c.Plan, c.ContributionAmount, c.Frequency,
        c.MaxMembers, memberCount, c.PayoutOrder, c.Status, c.CurrentCycle,
        c.StartDate, c.NextContributionDate, changed);
}

// ── Endpoint ──────────────────────────────────────────────────────────────────

public static class UpdateCircleEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPatch("/api/circles/{circleId:guid}",
            async (Guid circleId, UpdateCircleCommand cmd, IMediator mediator) =>
            {
                // Route wins over body, same as AddMemberEndpoint — so a mismatched
                // id in the payload can't retarget the update at another circle.
                var result = await mediator.Send(cmd with { CircleId = circleId });
                return Results.Ok(ApiResponse<UpdatedCircleDto>.Ok(result, "Circle updated."));
            })
        .WithName("UpdateCircle")
        .WithTags("Circles");
        //.RequireAuthorization();
}
