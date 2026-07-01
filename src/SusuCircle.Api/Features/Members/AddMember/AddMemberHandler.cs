using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Nomba;
using SusuCircle.Api.Common.Persistence;
using SusuCircle.Api.Common.Services;

namespace SusuCircle.Api.Features.Members.AddMember;

// ── Request / Response ────────────────────────────────────────────────────────

// NOTE: no VirtualAccountNumber/VirtualAccountId/BankName here — those come from
// Nomba now, not from the client. The client only ever sends name/phone/email.
public record AddMemberCommand(
    Guid CircleId,
    string Name,
    string Phone,
    string? Email) : IRequest<MemberDto>;

public record MemberDto(
    Guid Id, Guid CircleId, string Name, string Phone, string? Email,
    int PayoutPosition, string? VirtualAccountNumber, string? BankName,
    MemberStatus Status, int CreditScore, string CreditTier);

// ── Validator ─────────────────────────────────────────────────────────────────

public class AddMemberValidator : AbstractValidator<AddMemberCommand>
{
    public AddMemberValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Phone).NotEmpty().Matches(@"^0[7-9]\d{9}$")
            .WithMessage("Enter a valid Nigerian phone number.");
        RuleFor(x => x.Email).EmailAddress().When(x => x.Email is not null);
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class AddMemberHandler(
    AppDbContext db,
    INombaClient nomba,
    INotificationService notifications,
    ILogger<AddMemberHandler> logger)
    : IRequestHandler<AddMemberCommand, MemberDto>
{
    public async Task<MemberDto> Handle(AddMemberCommand cmd, CancellationToken ct)
    {
        var circle = await db.Circles
            .Include(c => c.Members)
            .FirstOrDefaultAsync(c => c.Id == cmd.CircleId, ct)
            ?? throw new NotFoundException(nameof(Circle), cmd.CircleId);

        if (circle.Members.Count(m => m.Status == MemberStatus.Active) >= circle.MaxMembers)
            throw new ConflictException($"Circle '{circle.Name}' is full ({circle.MaxMembers} members maximum).");

        if (circle.Members.Any(m => m.Phone == cmd.Phone))
            throw new ConflictException("A member with this phone number already exists in this circle.");

        // RULE: BAM forces payout position 0; ADASHI auto-increments sequentially.
        int calculatedPosition = 0;
        if (circle.Plan == PlanType.ADASHI)
        {
            var maxCurrentPosition = circle.Members.Any() ? circle.Members.Max(m => m.PayoutPosition) : 0;
            calculatedPosition = maxCurrentPosition + 1;
        }

        var member = new Member
        {
            Id = Guid.NewGuid(),
            CircleId = cmd.CircleId,
            Name = cmd.Name,
            Phone = cmd.Phone,
            Email = cmd.Email,
            PayoutPosition = calculatedPosition,
            Status = MemberStatus.Active,
        };

        // ── Provision Nomba virtual account (backend-owned, authoritative) ──
        // member.Id is the account reference, so this is idempotent per member.
        // Called BEFORE SaveChangesAsync — if Nomba fails, nothing is persisted
        // and the whole request fails cleanly (per your "always call Nomba, fail
        // if it errors" decision — no fallback to a client-supplied VA).
        VirtualAccountResponse va;
        try
        {
            va = await nomba.CreateVirtualAccountAsync(new CreateVirtualAccountRequest(
                AccountName: cmd.Name,
                AccountReference: member.Id.ToString(),
                CustomerPhone: cmd.Phone,
                CustomerEmail: cmd.Email), ct);
        }
        catch (NombaApiException ex)
        {
            logger.LogError(ex, "Nomba VA provisioning failed for {Name} ({Phone}) in circle {CircleId}",
                cmd.Name, cmd.Phone, cmd.CircleId);
            throw new NombaApiException($"Failed to provision virtual account for {cmd.Name}: {ex.Message}");
        }

        member.VirtualAccountId = va.AccountId;
        member.VirtualAccountNumber = va.AccountNumber;
        member.BankName = va.BankName;

        db.Members.Add(member);

        // First-cycle contribution record for this member
        db.Contributions.Add(new Contribution
        {
            Id = Guid.NewGuid(),
            MemberId = member.Id,
            CircleId = cmd.CircleId,
            CycleNumber = circle.CurrentCycle,
            ExpectedAmount = circle.ContributionAmount,
            DueDate = circle.NextContributionDate,
        });

        // Activate circle on first member added
        if (circle.Status == CircleStatus.Setup)
            circle.Status = CircleStatus.Active;

        await db.SaveChangesAsync(ct);

        // Welcome email with real VA details
        if (!string.IsNullOrWhiteSpace(cmd.Email))
        {
            await notifications.SendMemberWelcomeEmailAsync(
                cmd.Email,
                cmd.Name,
                circle.Name,
                va.AccountNumber,
                circle.ContributionAmount);
        }

        // In-app notification timeline — VA is guaranteed present now, no guard needed
        await notifications.SendAsync(member.Id, NotificationType.MemberAdded,
            "Welcome to the circle!",
            $"Your contribution account is {va.AccountNumber} ({va.BankName}). " +
            $"Save it as a beneficiary. Contribution of ₦{circle.ContributionAmount:N0} due by {circle.NextContributionDate:dd MMM yyyy}.",
            ct);

        logger.LogInformation("Member {MemberId} added to circle {CircleId} with VA {Va}",
            member.Id, circle.Id, va.AccountNumber);

        return MapToDto(member);
    }

    public static MemberDto MapToDto(Member m) => new(
        m.Id, m.CircleId, m.Name, m.Phone, m.Email,
        m.PayoutPosition, m.VirtualAccountNumber, m.BankName,
        m.Status, m.CreditScore, m.CreditTier);
}

// ── Endpoint ──────────────────────────────────────────────────────────────────

public static class AddMemberEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/circles/{circleId:guid}/members",
            async (Guid circleId, AddMemberCommand cmd, IMediator mediator) =>
            {
                var normalizedCmd = cmd with { CircleId = circleId };
                var result = await mediator.Send(normalizedCmd);

                return Results.Created($"/api/members/{result.Id}",
                    ApiResponse<MemberDto>.Ok(result, "Member added and virtual account provisioned."));
            })
        .WithName("AddMember")
        .WithTags("Members")
        .AllowAnonymous();
}