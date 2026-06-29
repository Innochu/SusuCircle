//using FluentValidation;
//using MediatR;
//using Microsoft.EntityFrameworkCore;
//using SusuCircle.Api.Common.Exceptions;
//using SusuCircle.Api.Common.Models;
//using SusuCircle.Api.Common.Nomba;
//using SusuCircle.Api.Common.Persistence;
//using SusuCircle.Api.Common.Services;

//namespace SusuCircle.Api.Features.Members.AddMember;

//// ── Request / Response ────────────────────────────────────────────────────────

//public record AddMemberCommand(Guid CircleId, string Name, string Phone, string? Email) : IRequest<MemberDto>;

//public record MemberDto(
//    Guid Id, Guid CircleId, string Name, string Phone, string? Email,
//    int PayoutPosition, string? VirtualAccountNumber, string? BankName,
//    MemberStatus Status, int CreditScore, string CreditTier);

//// ── Validator ─────────────────────────────────────────────────────────────────

//public class AddMemberValidator : AbstractValidator<AddMemberCommand>
//{
//    public AddMemberValidator()
//    {
//        RuleFor(x => x.Name).NotEmpty().MaximumLength(120);
//        RuleFor(x => x.Phone).NotEmpty().Matches(@"^0[7-9]\d{9}$").WithMessage("Enter a valid Nigerian phone number.");
//        RuleFor(x => x.Email).EmailAddress().When(x => x.Email is not null);
//    }
//}

//// ── Handler ───────────────────────────────────────────────────────────────────

//public class AddMemberHandler(AppDbContext db, INombaClient nomba, INotificationService notifications)
//    : IRequestHandler<AddMemberCommand, MemberDto>
//{
//    public async Task<MemberDto> Handle(AddMemberCommand cmd, CancellationToken ct)
//    {
//        var circle = await db.Circles
//            .Include(c => c.Members)
//            .FirstOrDefaultAsync(c => c.Id == cmd.CircleId, ct)
//            ?? throw new NotFoundException(nameof(Circle), cmd.CircleId);

//        if (circle.Members.Count(m => m.Status == MemberStatus.Active) >= circle.MaxMembers)
//            throw new ConflictException($"Circle '{circle.Name}' is full ({circle.MaxMembers} members maximum).");

//        // Check duplicate phone in circle
//        if (circle.Members.Any(m => m.Phone == cmd.Phone))
//            throw new ConflictException("A member with this phone number already exists in this circle.");

//        var position = circle.Members.Count + 1;

//        // Create member record first (id needed for VA reference)
//        var member = new Member
//        {
//            Id            = Guid.NewGuid(),
//            CircleId      = cmd.CircleId,
//            Name          = cmd.Name,
//            Phone         = cmd.Phone,
//            Email         = cmd.Email,
//            PayoutPosition = position,
//            Status        = MemberStatus.Active,
//        };

//        // Provision Nomba virtual account
//        var vaRequest = new CreateVirtualAccountRequest(
//            AccountName:   cmd.Name,
//            AccountReference: member.Id.ToString(),
//            CustomerPhone: cmd.Phone,
//            CustomerEmail: cmd.Email);

//        VirtualAccountResponse va;
//        try
//        {
//            va = await nomba.CreateVirtualAccountAsync(vaRequest, ct);
//        }
//        catch (NombaApiException ex)
//        {
//            throw new NombaApiException($"Failed to provision virtual account for {cmd.Name}: {ex.Message}");
//        }

//        member.VirtualAccountId     = va.AccountId;
//        member.VirtualAccountNumber = va.AccountNumber;
//        member.BankName             = va.BankName;

//        db.Members.Add(member);

//        // Create first cycle contribution record
//        db.Contributions.Add(new Contribution
//        {
//            Id             = Guid.NewGuid(),
//            MemberId       = member.Id,
//            CircleId       = cmd.CircleId,
//            CycleNumber    = circle.CurrentCycle,
//            ExpectedAmount = circle.ContributionAmount,
//            DueDate        = circle.NextContributionDate,
//        });

//        // Activate circle once first member is added
//        if (circle.Status == CircleStatus.Setup)
//            circle.Status = CircleStatus.Active;

//        await db.SaveChangesAsync(ct);

//        // Notify member of their VA number
//        await notifications.SendAsync(member.Id, NotificationType.MemberAdded,
//            "Welcome to the circle!",
//            $"Your unique contribution account number is {va.AccountNumber} ({va.BankName}). " +
//            $"Save it as a beneficiary. Your contribution of ₦{circle.ContributionAmount:N0} is due by {circle.NextContributionDate:dd MMM yyyy}.",
//            ct);

//        return MapToDto(member);
//    }

//    public static MemberDto MapToDto(Member m) => new(
//        m.Id, m.CircleId, m.Name, m.Phone, m.Email,
//        m.PayoutPosition, m.VirtualAccountNumber, m.BankName,
//        m.Status, m.CreditScore, m.CreditTier);
//}

//// ── Endpoint ──────────────────────────────────────────────────────────────────

//public static class AddMemberEndpoint
//{
//    public static void Map(IEndpointRouteBuilder app) =>
//        app.MapPost("/api/circles/members",
//            async (AddMemberCommand cmd, IMediator mediator) =>
//            {
//                var result = await mediator.Send(cmd with { CircleId = cmd.CircleId });
//                return Results.Created($"/api/members/{result.Id}", ApiResponse<MemberDto>.Ok(result, "Member added and virtual account provisioned."));
//            })
//        .WithName("AddMember")
//        .WithTags("Members");
//        //.RequireAuthorization();
//}



using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;
using SusuCircle.Api.Common.Services;

namespace SusuCircle.Api.Features.Members.AddMember;

// ── Request / Response ────────────────────────────────────────────────────────

public record AddMemberCommand(
    Guid CircleId,
    string Name,
    string Phone,
    string? Email,
    string? VirtualAccountNumber,
    string? VirtualAccountId,
    string? BankName) : IRequest<MemberDto>;

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

public class AddMemberHandler(AppDbContext db, INotificationService notifications)
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

        // RULE: Force payout position to 0 for BAM, auto-increment only for ADASHI groups
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
            VirtualAccountId = cmd.VirtualAccountId,
            VirtualAccountNumber = cmd.VirtualAccountNumber,
            BankName = cmd.BankName ?? "Nomba MFB",
        };

        db.Members.Add(member);

        // Create contribution record for current cycle
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

        // Perform transactional database persistence first
        await db.SaveChangesAsync(ct);

        // REQUIREMENT: Email send to newly added member containing welcome state and virtual account info
        if (!string.IsNullOrWhiteSpace(cmd.Email))
        {
            await notifications.SendMemberWelcomeEmailAsync(
                cmd.Email,
                cmd.Name,
                circle.Name,
                cmd.VirtualAccountNumber ?? "Pending Activation",
                circle.ContributionAmount
            );
        }

        // Emit inside application event notifications timeline
        if (!string.IsNullOrEmpty(cmd.VirtualAccountNumber))
        {
            await notifications.SendAsync(member.Id, NotificationType.MemberAdded,
                "Welcome to the circle!",
                $"Your contribution account is {cmd.VirtualAccountNumber} ({member.BankName}). " +
                $"Save it as a beneficiary. Contribution of ₦{circle.ContributionAmount:N0} due by {circle.NextContributionDate:dd MMM yyyy}.",
                ct);
        }

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
                // Sync the path parameters explicitly into the payload context
                var normalizedCmd = cmd with { CircleId = circleId };
                var result = await mediator.Send(normalizedCmd);

                return Results.Created($"/api/members/{result.Id}",
                    ApiResponse<MemberDto>.Ok(result, "Member saved successfully and welcome notifications queued."));
            })
        .WithName("AddMember")
        .WithTags("Members")
        .AllowAnonymous();
}
