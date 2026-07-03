using System.Security.Cryptography;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Nomba;
using SusuCircle.Api.Common.Persistence;
using SusuCircle.Api.Common.Services;

namespace SusuCircle.Api.Features.Members.AddMember;

// ══════════════════════════════════════════════════════════════════════════════
// REQUIRES a new column on Member: PasswordHash (string?).
// Add to Member model (Common/Models/... wherever Member is defined):
//     public string? PasswordHash { get; set; }
// Then migrate:
//     Add-Migration MemberPasswordHash   (PMC)
//     dotnet ef migrations add MemberPasswordHash   (CLI)
//
// STILL NEEDED SEPARATELY: a member-login endpoint that verifies this hash
// (mirrors LoginHandler for Admin, but querying Members by Email instead).
// This file only ISSUES the credential — nothing yet checks it at login time.
// ══════════════════════════════════════════════════════════════════════════════

// ── Request / Response ────────────────────────────────────────────────────────

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

        // ── Generate + hash a login password, same as RegisterHandler does for Admins ──
        // Plaintext is used ONLY to send in the welcome email below — it is never
        // stored anywhere; only the BCrypt hash is persisted.
        var temporaryPassword = GenerateTemporaryPassword();

        var member = new Member
        {
            Id = Guid.NewGuid(),
            CircleId = cmd.CircleId,
            Name = cmd.Name,
            Phone = cmd.Phone,
            Email = cmd.Email,
            PayoutPosition = calculatedPosition,
            Status = MemberStatus.Active,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(temporaryPassword),
        };

        // TEMPORARY — TEST ONLY. Logs the plaintext password so you can log in
        // during testing if the welcome email doesn't arrive. This is a real
        // credential in plaintext in your logs — REMOVE this line before any
        // real deployment, demo recording, or judging session. LogWarning (not
        // LogInformation) so it's easy to find and easy to remember to delete.
        logger.LogWarning(
            "TEST ONLY — temporary password for {Name} ({Email}): {Password}",
            cmd.Name, cmd.Email ?? "no email", temporaryPassword);

        // ── Provision Nomba virtual account (backend-owned, authoritative) ──
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

        db.Contributions.Add(new Contribution
        {
            Id = Guid.NewGuid(),
            MemberId = member.Id,
            CircleId = cmd.CircleId,
            CycleNumber = circle.CurrentCycle,
            ExpectedAmount = circle.ContributionAmount,
            DueDate = circle.NextContributionDate,
        });

        if (circle.Status == CircleStatus.Setup)
            circle.Status = CircleStatus.Active;

        await db.SaveChangesAsync(ct);

        // Welcome email — now includes login credentials alongside the VA details.
        if (!string.IsNullOrWhiteSpace(cmd.Email))
        {
            await notifications.SendMemberWelcomeEmailAsync(
                cmd.Email,
                cmd.Name,
                circle.Name,
                va.AccountNumber,
                circle.ContributionAmount,
                temporaryPassword);
        }

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

    // Generates an 10-character password meeting the same complexity rules as
    // RegisterValidator (uppercase + digit present) using a cryptographically
    // random source — not Random/Guid, since this is a real credential.
    private static string GenerateTemporaryPassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";      // no I/O — avoid confusion with 1/0
        const string lower = "abcdefghijkmnpqrstuvwxyz";
        const string digits = "23456789";                      // no 0/1 — avoid confusion with O/l
        const string all = upper + lower + digits;

        Span<byte> buffer = stackalloc byte[10];
        RandomNumberGenerator.Fill(buffer);

        var chars = new char[10];
        for (int i = 0; i < chars.Length; i++)
            chars[i] = all[buffer[i] % all.Length];

        // Guarantee at least one uppercase and one digit, deterministically at
        // fixed positions, without weakening the rest of the random spread.
        chars[0] = upper[buffer[0] % upper.Length];
        chars[1] = digits[buffer[1] % digits.Length];

        return new string(chars);
    }
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