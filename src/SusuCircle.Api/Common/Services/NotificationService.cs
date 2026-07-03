using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Common.Services;

// ══════════════════════════════════════════════════════════════════════════════
// CHANGED: SendMemberWelcomeEmailAsync now takes an extra `temporaryPassword`
// parameter and includes a login-credentials block in the email, mirroring
// what an admin gets after using /api/auth/register — email + password, ready
// to log in immediately. Every call site (AddMemberHandler) must pass this.
// ══════════════════════════════════════════════════════════════════════════════

public interface INotificationService
{
    Task SendAsync(Guid memberId, NotificationType type, string title, string body, CancellationToken ct = default);
    Task SendBulkAsync(IEnumerable<Guid> memberIds, NotificationType type, string title, string body, CancellationToken ct = default);
    Task SendAdminWelcomeEmailAsync(string email, string adminName);
    Task SendMemberWelcomeEmailAsync(string email, string memberName, string circleName, string virtualAccount, decimal amount, string temporaryPassword);
    Task SendCircleCreditedEmailAsync(string email, string memberName, string circleName, decimal amountCredited, string status);
}

public class NotificationService(AppDbContext db, IEmailSender emailSender, ILogger<NotificationService> logger) : INotificationService
{
    public async Task SendAsync(Guid memberId, NotificationType type, string title, string body, CancellationToken ct = default)
    {
        db.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(),
            MemberId = memberId,
            Type = type,
            Title = title,
            Body = body,
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("In-app notification saved for member {MemberId}: {Title}", memberId, title);
    }

    public async Task SendBulkAsync(IEnumerable<Guid> memberIds, NotificationType type, string title, string body, CancellationToken ct = default)
    {
        var list = memberIds.ToList();
        db.Notifications.AddRange(list.Select(id => new Notification
        {
            Id = Guid.NewGuid(),
            MemberId = id,
            Type = type,
            Title = title,
            Body = body,
        }));
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Bulk in-app notification saved for {Count} members: {Title}", list.Count, title);
    }

    public async Task SendAdminWelcomeEmailAsync(string email, string adminName)
    {
        var subject = "Welcome to Susu Circle — Your deployment is active!";
        var html = WrapInLayout($"""
            <h2 style="color:#1a1a2e;">Welcome, {Encode(adminName)}! 🎉</h2>
            <p>Your <strong>Susu Circle</strong> deployment is now active and ready to use.</p>
            <p>You can start creating circles, onboarding members, and managing contributions from your admin dashboard.</p>
            <p style="margin-top:32px;">If you have any questions, reply to this email — we're here to help.</p>
        """);

        await SendEmailAsync(email, subject, html);
        logger.LogInformation("Admin welcome email sent to {Email}", email);
    }

    public async Task SendMemberWelcomeEmailAsync(
        string email, string memberName, string circleName, string virtualAccount,
        decimal amount, string temporaryPassword)
    {
        var subject = $"You've been added to {circleName} on Susu Circle";
        var html = WrapInLayout($"""
            <h2 style="color:#1a1a2e;">Hello, {Encode(memberName)}! 👋</h2>
            <p>You have been successfully onboarded to <strong>{Encode(circleName)}</strong> on Susu Circle.</p>

            <div style="background:#f4f6ff;border-left:4px solid #4f46e5;padding:16px 20px;border-radius:4px;margin:24px 0;">
                <p style="margin:0 0 8px;font-size:13px;color:#6b7280;text-transform:uppercase;letter-spacing:.5px;">Your Unique Virtual Account</p>
                <p style="margin:0;font-size:24px;font-weight:700;letter-spacing:2px;color:#1a1a2e;">{Encode(virtualAccount)}</p>
                <p style="margin:8px 0 0;font-size:13px;color:#6b7280;">Use this account number for all your contributions to this circle.</p>
            </div>

            <div style="background:#fef9f0;border-left:4px solid #d97706;padding:16px 20px;border-radius:4px;margin:24px 0;">
                <p style="margin:0 0 8px;font-size:13px;color:#92400e;text-transform:uppercase;letter-spacing:.5px;">Your Login Details</p>
                <table style="width:100%;border-collapse:collapse;">
                    <tr>
                        <td style="padding:4px 0;color:#6b7280;font-size:13px;">Email</td>
                        <td style="padding:4px 0;font-weight:600;text-align:right;font-size:14px;">{Encode(email)}</td>
                    </tr>
                    <tr>
                        <td style="padding:4px 0;color:#6b7280;font-size:13px;">Temporary Password</td>
                        <td style="padding:4px 0;font-weight:700;text-align:right;font-size:16px;letter-spacing:1px;font-family:monospace;">{Encode(temporaryPassword)}</td>
                    </tr>
                </table>
                <p style="margin:12px 0 0;font-size:13px;color:#92400e;">Use these to log in and view your contribution status. We recommend changing your password after first login.</p>
            </div>

            <table style="width:100%;border-collapse:collapse;margin-top:8px;">
                <tr>
                    <td style="padding:10px 0;border-bottom:1px solid #e5e7eb;color:#6b7280;">Circle Name</td>
                    <td style="padding:10px 0;border-bottom:1px solid #e5e7eb;font-weight:600;text-align:right;">{Encode(circleName)}</td>
                </tr>
                <tr>
                    <td style="padding:10px 0;color:#6b7280;">Round Contribution</td>
                    <td style="padding:10px 0;font-weight:600;text-align:right;">₦{amount:N2}</td>
                </tr>
            </table>

            <p style="margin-top:24px;">Please ensure you transfer exactly <strong>₦{amount:N2}</strong> to the virtual account above when your round is due.</p>
        """);

        await SendEmailAsync(email, subject, html);
        logger.LogInformation("Member welcome email sent to {Email} for circle {Circle}", email, circleName);
    }

    public async Task SendCircleCreditedEmailAsync(string email, string memberName, string circleName, decimal amountCredited, string status)
    {
        var isSuccess = status.Equals("success", StringComparison.OrdinalIgnoreCase)
                     || status.Equals("completed", StringComparison.OrdinalIgnoreCase);

        var statusColor = isSuccess ? "#16a34a" : "#d97706";
        var statusBg = isSuccess ? "#f0fdf4" : "#fffbeb";
        var subject = $"Transaction Alert — Contribution to {circleName}";

        var html = WrapInLayout($"""
            <h2 style="color:#1a1a2e;">Transaction Alert 🔔</h2>
            <p>Hi <strong>{Encode(memberName)}</strong>, your contribution to <strong>{Encode(circleName)}</strong> has been processed via auto-reconciliation.</p>

            <table style="width:100%;border-collapse:collapse;margin:24px 0;">
                <tr>
                    <td style="padding:10px 0;border-bottom:1px solid #e5e7eb;color:#6b7280;">Circle</td>
                    <td style="padding:10px 0;border-bottom:1px solid #e5e7eb;font-weight:600;text-align:right;">{Encode(circleName)}</td>
                </tr>
                <tr>
                    <td style="padding:10px 0;border-bottom:1px solid #e5e7eb;color:#6b7280;">Amount</td>
                    <td style="padding:10px 0;border-bottom:1px solid #e5e7eb;font-weight:600;text-align:right;">₦{amountCredited:N2}</td>
                </tr>
                <tr>
                    <td style="padding:10px 0;color:#6b7280;">Status</td>
                    <td style="padding:10px 0;text-align:right;">
                        <span style="background:{statusBg};color:{statusColor};padding:3px 10px;border-radius:12px;font-size:13px;font-weight:600;">{Encode(status)}</span>
                    </td>
                </tr>
            </table>

            <p>If you did not initiate this transaction or have questions, please contact your circle admin immediately.</p>
        """);

        await SendEmailAsync(email, subject, html);
        logger.LogInformation("Circle credited email sent to {Email} — ₦{Amount} to {Circle} [{Status}]", email, amountCredited, circleName, status);
    }

    private async Task SendEmailAsync(string toAddress, string subject, string htmlBody)
        => await emailSender.SendEmailAsync(toAddress, subject, htmlBody);

    private static string WrapInLayout(string content) => $"""
        <!DOCTYPE html>
        <html lang="en">
        <head><meta charset="UTF-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head>
        <body style="margin:0;padding:0;background:#f3f4f6;font-family:'Segoe UI',Arial,sans-serif;color:#1a1a2e;">
          <table width="100%" cellpadding="0" cellspacing="0" style="background:#f3f4f6;padding:40px 0;">
            <tr><td align="center">
              <table width="600" cellpadding="0" cellspacing="0" style="background:#ffffff;border-radius:8px;overflow:hidden;box-shadow:0 1px 4px rgba(0,0,0,.08);">

                <!-- Header -->
                <tr><td style="background:#4f46e5;padding:28px 40px;">
                  <h1 style="margin:0;color:#ffffff;font-size:22px;letter-spacing:-.3px;">Susu Circle</h1>
                  <p style="margin:4px 0 0;color:#c7d2fe;font-size:13px;">Rotating Savings, Reimagined</p>
                </td></tr>

                <!-- Body -->
                <tr><td style="padding:36px 40px;font-size:15px;line-height:1.7;color:#374151;">
                  {content}
                </td></tr>

                <!-- Footer -->
                <tr><td style="background:#f9fafb;padding:20px 40px;border-top:1px solid #e5e7eb;font-size:12px;color:#9ca3af;text-align:center;">
                  © {DateTime.UtcNow.Year} Susu Circle. This is an automated message — please do not reply directly.
                </td></tr>

              </table>
            </td></tr>
          </table>
        </body>
        </html>
        """;

    private static string Encode(string s) => System.Net.WebUtility.HtmlEncode(s);
}