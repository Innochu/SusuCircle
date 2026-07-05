// ══════════════════════════════════════════════════════════════════════════════
// REQUIRES two new columns on BOTH Admin and Member:
//     public string? ResetCode { get; set; }
//     public DateTime? ResetCodeExpiry { get; set; }
// Migrate: Add-Migration PasswordResetCode / dotnet ef migrations add PasswordResetCode
//
// FLOW: 6-digit numeric code (not a long token) — easier to type/read off an
// email on a phone, same UX pattern as most bank/fintech OTP flows.
//   1. POST /api/auth/forgot-password { email }        -> emails a 6-digit code, 15 min expiry
//   2. POST /api/auth/reset-password  { email, code, newPassword, confirmNewPassword }
// Checks Admins first, then Members — same pattern as the unified LoginHandler.
// ══════════════════════════════════════════════════════════════════════════════

using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;
using SusuCircle.Api.Common.Services;
using System.Security.Cryptography;

namespace SusuCircle.Api.Features.Auth.ForgotPassword;

// ── Forgot password (request a code) ────────────────────────────────────────

public record ForgotPasswordCommand(string Email) : IRequest<bool>;

public class ForgotPasswordValidator : AbstractValidator<ForgotPasswordCommand>
{
    public ForgotPasswordValidator() =>
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
}

public class ForgotPasswordHandler(AppDbContext db, IEmailSender emailSender)
    : IRequestHandler<ForgotPasswordCommand, bool>
{
    public async Task<bool> Handle(ForgotPasswordCommand cmd, CancellationToken ct)
    {
        var email = cmd.Email.ToLower();
        var code = GenerateCode();
        var expiry = DateTime.UtcNow.AddMinutes(15);

        var admin = await db.Admins.FirstOrDefaultAsync(a => a.Email == email, ct);
        if (admin is not null)
        {
            admin.ResetCode = code;
            admin.ResetCodeExpiry = expiry;
            await db.SaveChangesAsync(ct);
            await SendCodeEmail(email, admin.Name, code, ct);
            return true;
        }

        var member = await db.Members
            .Where(m => m.Email == email)
            .OrderByDescending(m => !string.IsNullOrEmpty(m.PasswordHash))
            .FirstOrDefaultAsync(ct);
        if (member is not null)
        {
            member.ResetCode = code;
            member.ResetCodeExpiry = expiry;
            await db.SaveChangesAsync(ct);
            await SendCodeEmail(email, member.Name, code, ct);
            return true;
        }

        // IMPORTANT: return true (not found) either way — a forgot-password
        // endpoint that says "no account with that email" lets an attacker
        // enumerate which emails are registered. Always respond the same way.
        return true;
    }

    private async Task SendCodeEmail(string email, string name, string code, CancellationToken ct)
    {
        var html = $"""
            <p>Hello {name},</p>
            <p>Your password reset code is:</p>
            <p style="font-size:28px;font-weight:700;letter-spacing:4px;">{code}</p>
            <p>This code expires in 15 minutes. If you didn't request this, ignore this email.</p>
            """;
        await emailSender.SendEmailAsync(email, "Your Susu Circle password reset code", html, ct);
    }

    private static string GenerateCode()
    {
        var bytes = new byte[4];
        RandomNumberGenerator.Fill(bytes);
        var n = BitConverter.ToUInt32(bytes, 0) % 1_000_000;
        return n.ToString("D6"); // always 6 digits, zero-padded
    }
}

public static class ForgotPasswordEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/auth/forgot-password", async (ForgotPasswordCommand cmd, IMediator mediator) =>
        {
            await mediator.Send(cmd);
            return Results.Ok(ApiResponse<object>.Ok(new { },
                "If an account exists with that email, a reset code has been sent."));
        })
        .WithName("ForgotPassword")
        .WithTags("Auth")
        .AllowAnonymous();
}

// ── Reset password (submit the code + new password) ────────────────────────

public record ResetPasswordCommand(
    string Email, string Code, string NewPassword, string ConfirmNewPassword) : IRequest<bool>;

public class ResetPasswordValidator : AbstractValidator<ResetPasswordCommand>
{
    public ResetPasswordValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Code).NotEmpty().Length(6);
        RuleFor(x => x.NewPassword)
            .NotEmpty().MinimumLength(8)
            .Matches(@"[A-Z]").WithMessage("Password must contain at least one uppercase letter.")
            .Matches(@"[0-9]").WithMessage("Password must contain at least one number.");
        RuleFor(x => x.ConfirmNewPassword).Equal(x => x.NewPassword)
            .WithMessage("Passwords do not match.");
    }
}

public class ResetPasswordHandler(AppDbContext db) : IRequestHandler<ResetPasswordCommand, bool>
{
    public async Task<bool> Handle(ResetPasswordCommand cmd, CancellationToken ct)
    {
        var email = cmd.Email.ToLower();

        var admin = await db.Admins.FirstOrDefaultAsync(a => a.Email == email, ct);
        if (admin is not null)
        {
            ValidateCode(admin.ResetCode, admin.ResetCodeExpiry, cmd.Code);
            admin.PasswordHash = BCrypt.Net.BCrypt.HashPassword(cmd.NewPassword);
            admin.ResetCode = null;
            admin.ResetCodeExpiry = null;
            admin.RefreshToken = null;       // force re-login everywhere after a reset
            admin.RefreshTokenExpiry = null;
            await db.SaveChangesAsync(ct);
            return true;
        }

        var member = await db.Members
            .Where(m => m.Email == email)
            .OrderByDescending(m => !string.IsNullOrEmpty(m.PasswordHash))
            .FirstOrDefaultAsync(ct);
        if (member is not null)
        {
            ValidateCode(member.ResetCode, member.ResetCodeExpiry, cmd.Code);
            member.PasswordHash = BCrypt.Net.BCrypt.HashPassword(cmd.NewPassword);
            member.ResetCode = null;
            member.ResetCodeExpiry = null;
            member.RefreshToken = null;
            member.RefreshTokenExpiry = null;
            await db.SaveChangesAsync(ct);
            return true;
        }

        throw new UnauthorizedException("Invalid or expired reset code.");
    }

    private static void ValidateCode(string? storedCode, DateTime? expiry, string providedCode)
    {
        if (string.IsNullOrEmpty(storedCode) || expiry is null || expiry < DateTime.UtcNow)
            throw new UnauthorizedException("Invalid or expired reset code.");

        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(storedCode),
                System.Text.Encoding.UTF8.GetBytes(providedCode)))
        {
            throw new UnauthorizedException("Invalid or expired reset code.");
        }
    }
}

public static class ResetPasswordEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/auth/reset-password", async (ResetPasswordCommand cmd, IMediator mediator) =>
        {
            await mediator.Send(cmd);
            return Results.Ok(ApiResponse<object>.Ok(new { }, "Password reset successfully."));
        })
        .WithName("ResetPassword")
        .WithTags("Auth")
        .AllowAnonymous();
}