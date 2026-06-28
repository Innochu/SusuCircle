using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;
using SusuCircle.Api.Common.Services;

namespace SusuCircle.Api.Features.Auth.RefreshToken;

public record RefreshTokenCommand(string AccessToken, string RefreshToken) : IRequest<RefreshTokenResponse>;
public record RefreshTokenResponse(string AccessToken, string RefreshToken);

public class RefreshTokenValidator : AbstractValidator<RefreshTokenCommand>
{
    public RefreshTokenValidator()
    {
        RuleFor(x => x.AccessToken).NotEmpty();
        RuleFor(x => x.RefreshToken).NotEmpty();
    }
}

public class RefreshTokenHandler(AppDbContext db, IJwtService jwt) : IRequestHandler<RefreshTokenCommand, RefreshTokenResponse>
{
    public async Task<RefreshTokenResponse> Handle(RefreshTokenCommand cmd, CancellationToken ct)
    {
        var principal = jwt.GetPrincipalFromExpiredToken(cmd.AccessToken)
            ?? throw new UnauthorizedException("Invalid access token.");

        var adminId = Guid.Parse(principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? principal.FindFirst("sub")?.Value
            ?? throw new UnauthorizedException("Token missing subject."));

        var admin = await db.Admins.FirstOrDefaultAsync(a => a.Id == adminId, ct)
            ?? throw new UnauthorizedException("Admin not found.");

        if (admin.RefreshToken != cmd.RefreshToken || admin.RefreshTokenExpiry < DateTime.UtcNow)
            throw new UnauthorizedException("Refresh token is invalid or expired.");

        var newAccess  = jwt.GenerateAccessToken(admin);
        var newRefresh = jwt.GenerateRefreshToken();

        admin.RefreshToken       = newRefresh;
        admin.RefreshTokenExpiry = DateTime.UtcNow.AddDays(7);
        await db.SaveChangesAsync(ct);

        return new RefreshTokenResponse(newAccess, newRefresh);
    }
}

public static class RefreshTokenEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/auth/refresh", async (RefreshTokenCommand cmd, IMediator mediator) =>
        {
            var result = await mediator.Send(cmd);
            return Results.Ok(ApiResponse<RefreshTokenResponse>.Ok(result));
        })
        .WithName("RefreshToken")
        .WithTags("Auth")
        .AllowAnonymous();
}
