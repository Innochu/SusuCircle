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

public record LoginResponse(string AccessToken, string RefreshToken, AdminDto Admin);

public record AdminDto(Guid Id, string Name, string Email, string Phone);

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
        var admin = await db.Admins.FirstOrDefaultAsync(a => a.Email == cmd.Email.ToLower(), ct)
            ?? throw new UnauthorizedException("Invalid email or password.");

        if (!BCrypt.Net.BCrypt.Verify(cmd.Password, admin.PasswordHash))
            throw new UnauthorizedException("Invalid email or password.");

        var accessToken  = jwt.GenerateAccessToken(admin);
        var refreshToken = jwt.GenerateRefreshToken();

        admin.RefreshToken       = refreshToken;
        admin.RefreshTokenExpiry = DateTime.UtcNow.AddDays(7);
        await db.SaveChangesAsync(ct);

        return new LoginResponse(
            accessToken,
            refreshToken,
            new AdminDto(admin.Id, admin.Name, admin.Email, admin.Phone));
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
