using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;
using SusuCircle.Api.Common.Services;

namespace SusuCircle.Api.Features.Auth.Register;

// ── Request / Response ────────────────────────────────────────────────────────

public record RegisterCommand(
    string Name,
    string Email,
    string Phone,
    string Password,
    string ConfirmPassword) : IRequest<RegisterResponse>;

public record RegisterResponse(string AccessToken, string RefreshToken, AdminDto Admin);

public record AdminDto(Guid Id, string Name, string Email, string Phone);

// ── Validator ─────────────────────────────────────────────────────────────────

public class RegisterValidator : AbstractValidator<RegisterCommand>
{
    public RegisterValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name is required.")
            .MaximumLength(120).WithMessage("Name must not exceed 120 characters.");

        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("A valid email address is required.");

        RuleFor(x => x.Phone)
            .NotEmpty().WithMessage("Phone number is required.")
            .Matches(@"^0[7-9]\d{9}$").WithMessage("Enter a valid Nigerian phone number (e.g. 08012345678).");

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required.")
            .MinimumLength(8).WithMessage("Password must be at least 8 characters.")
            .Matches(@"[A-Z]").WithMessage("Password must contain at least one uppercase letter.")
            .Matches(@"[0-9]").WithMessage("Password must contain at least one number.");

        RuleFor(x => x.ConfirmPassword)
            .NotEmpty().WithMessage("Please confirm your password.")
            .Equal(x => x.Password).WithMessage("Passwords do not match.");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class RegisterHandler(AppDbContext db, IJwtService jwt) : IRequestHandler<RegisterCommand, RegisterResponse>
{
    public async Task<RegisterResponse> Handle(RegisterCommand cmd, CancellationToken ct)
    {
        // Check for duplicate email
        var emailTaken = await db.Admins.AnyAsync(a => a.Email == cmd.Email.ToLower(), ct);
        if (emailTaken)
            throw new ConflictException($"An account with email '{cmd.Email}' already exists.");

        // Check for duplicate phone
        var phoneTaken = await db.Admins.AnyAsync(a => a.Phone == cmd.Phone, ct);
        if (phoneTaken)
            throw new ConflictException($"An account with phone '{cmd.Phone}' already exists.");

        var admin = new Admin
        {
            Id = Guid.NewGuid(),
            Name = cmd.Name.Trim(),
            Email = cmd.Email.ToLower().Trim(),
            Phone = cmd.Phone.Trim(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(cmd.Password),
        };

        var accessToken = jwt.GenerateAccessToken(admin);
        var refreshToken = jwt.GenerateRefreshToken();

        admin.RefreshToken = refreshToken;
        admin.RefreshTokenExpiry = DateTime.UtcNow.AddDays(7);

        db.Admins.Add(admin);
        await db.SaveChangesAsync(ct);

        return new RegisterResponse(
            accessToken,
            refreshToken,
            new AdminDto(admin.Id, admin.Name, admin.Email, admin.Phone));
    }
}

// ── Endpoint ──────────────────────────────────────────────────────────────────

public static class RegisterEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/auth/register", async (RegisterCommand cmd, IMediator mediator) =>
        {
            var result = await mediator.Send(cmd);
            return Results.Created("/api/auth/register", ApiResponse<RegisterResponse>.Ok(result, "Account created successfully."));
        })
        .WithName("Register")
        .WithTags("Auth")
        .AllowAnonymous();
}
