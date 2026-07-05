using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Features.Auth.Logout;

// ══════════════════════════════════════════════════════════════════════════════
// Stateless-JWT logout: the access token itself can't be revoked early (it's
// just a signed blob, valid until it naturally expires — short-lived by
// design). What we CAN and should do is kill the refresh token, so even if
// someone has the old access token, once it expires they can't silently get
// a new one. Works for either Admin or Member — looks up by the refresh token
// itself rather than requiring the caller to say which table they're in.
// ══════════════════════════════════════════════════════════════════════════════

public record LogoutCommand(string RefreshToken) : IRequest<bool>;

public class LogoutValidator : AbstractValidator<LogoutCommand>
{
    public LogoutValidator() => RuleFor(x => x.RefreshToken).NotEmpty();
}

public class LogoutHandler(AppDbContext db) : IRequestHandler<LogoutCommand, bool>
{
    public async Task<bool> Handle(LogoutCommand cmd, CancellationToken ct)
    {
        var admin = await db.Admins.FirstOrDefaultAsync(a => a.RefreshToken == cmd.RefreshToken, ct);
        if (admin is not null)
        {
            admin.RefreshToken = null;
            admin.RefreshTokenExpiry = null;
            await db.SaveChangesAsync(ct);
            return true;
        }

        var member = await db.Members.FirstOrDefaultAsync(m => m.RefreshToken == cmd.RefreshToken, ct);
        if (member is not null)
        {
            member.RefreshToken = null;
            member.RefreshTokenExpiry = null;
            await db.SaveChangesAsync(ct);
            return true;
        }

        // Already logged out / unknown token — treat as success either way,
        // logout should never fail loudly from the caller's perspective.
        return true;
    }
}

public static class LogoutEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/auth/logout", async (LogoutCommand cmd, IMediator mediator) =>
        {
            await mediator.Send(cmd);
            return Results.Ok(ApiResponse<object>.Ok(new { }, "Logged out."));
        })
        .WithName("Logout")
        .WithTags("Auth")
        .AllowAnonymous();
}