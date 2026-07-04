// ══════════════════════════════════════════════════════════════════════════════
// REQUIRES (same as before, this doesn't remove that need — just avoids a
// second endpoint):
//
// 1. Member model: PasswordHash, RefreshToken, RefreshTokenExpiry columns.
//    Migrate: Add-Migration MemberAuth / dotnet ef migrations add MemberAuth
//
// 2. IJwtService needs an overload for Member:
//        string GenerateAccessToken(Member member);
//    Mirror GenerateAccessToken(Admin) exactly, but set a "role": "member"
//    claim instead of whatever the admin one uses. This distinction matters
//    the moment your admin endpoints move off AllowAnonymous() — without it,
//    a member's token is indistinguishable from an admin's to anything that
//    only checks "is this JWT valid."
//
// DESIGN: checks Admins FIRST, then Members, by email. If the same email
// somehow exists in both tables (shouldn't happen in normal use, but nothing
// currently prevents it), the admin record wins silently. Response includes
// a `role` field so the frontend knows which shape (Admin vs Member) to read
// and which view to route to — this is why the "Member view" toggle already
// visible in your screenshots makes sense as a concept.
// ══════════════════════════════════════════════════════════════════════════════

using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;
using SusuCircle.Api.Common.Services;

namespace SusuCircle.Api.Features.Auth.Login;

// ── Request / Response ────────────────────────────────────────────────────────

public record LoginCommand(string Email, string Password) : IRequest<LoginResponse>;

// Only ONE of Admin / Member is populated, indicated by Role.
public record LoginResponse(
    string AccessToken,
    string RefreshToken,
    string Role,              // "admin" | "member"
    AdminDto? Admin,
    MemberAuthDto? Member);

public record AdminDto(Guid Id, string Name, string Email, string Phone);

public record MemberAuthDto(
    Guid Id, string Name, string? Email, string Phone,
    Guid CircleId, int PayoutPosition, string? VirtualAccountNumber);

// ── Validator ─────────────────────────────────────────────────────────────────

public class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty().MinimumLength(6);
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class LoginHandler(AppDbContext db, IJwtService jwt) : IRequestHandler<LoginCommand, LoginResponse>
{
    public async Task<LoginResponse> Handle(LoginCommand cmd, CancellationToken ct)
    {
        var email = cmd.Email.ToLower();

        // ── Try admin first ──
        var admin = await db.Admins.FirstOrDefaultAsync(a => a.Email == email, ct);
        if (admin is not null)
        {
            if (!BCrypt.Net.BCrypt.Verify(cmd.Password, admin.PasswordHash))
                throw new UnauthorizedException("Invalid email or password.");

            var adminAccessToken = jwt.GenerateAccessToken(admin);
            var adminRefreshToken = jwt.GenerateRefreshToken();

            admin.RefreshToken = adminRefreshToken;
            admin.RefreshTokenExpiry = DateTime.UtcNow.AddDays(7);
            await db.SaveChangesAsync(ct);

            return new LoginResponse(
                adminAccessToken,
                adminRefreshToken,
                "admin",
                new AdminDto(admin.Id, admin.Name, admin.Email, admin.Phone),
                null);
        }

        // ── Fall back to member ──
        var member = await db.Members.FirstOrDefaultAsync(m => m.Email == email, ct);
        if (member is not null)
        {
            if (string.IsNullOrEmpty(member.PasswordHash) ||
                !BCrypt.Net.BCrypt.Verify(cmd.Password, member.PasswordHash))
            {
                throw new UnauthorizedException("Invalid email or password.");
            }

            if (member.Status != MemberStatus.Active)
                throw new UnauthorizedException("This member account is not active.");

            var memberAccessToken = jwt.GenerateAccessToken(member);   // requires the IJwtService overload
            var memberRefreshToken = jwt.GenerateRefreshToken();

            member.RefreshToken = memberRefreshToken;
            member.RefreshTokenExpiry = DateTime.UtcNow.AddDays(7);
            await db.SaveChangesAsync(ct);

            return new LoginResponse(
                memberAccessToken,
                memberRefreshToken,
                "member",
                null,
                new MemberAuthDto(
                    member.Id, member.Name, member.Email, member.Phone,
                    member.CircleId, member.PayoutPosition, member.VirtualAccountNumber));
        }

        // Neither table had this email — same generic message either way.
        throw new UnauthorizedException("Invalid email or password.");
    }
}

// ── Endpoint ──────────────────────────────────────────────────────────────────

public static class LoginEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/auth/login", async (LoginCommand cmd, IMediator mediator) =>
        {
            var result = await mediator.Send(cmd);
            return Results.Ok(ApiResponse<LoginResponse>.Ok(result, "Login successful."));
        })
        .WithName("Login")
        .WithTags("Auth")
        .AllowAnonymous();
}