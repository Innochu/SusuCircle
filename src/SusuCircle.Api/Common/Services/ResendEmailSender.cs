using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace SusuCircle.Api.Common.Services;

// ══════════════════════════════════════════════════════════════════════════════
// Sends email via Resend's HTTPS API instead of raw SMTP.
//
// WHY: your SMTP host (wajesmarthrms.website:587) times out at the TCP connect
// stage from Render — either Render blocks outbound SMTP, or the mail host
// blocks cloud/datacenter IPs. Either way, raw SMTP is unreliable from a PaaS.
// Resend sends over HTTPS (port 443), which is never blocked.
//
// WORKS IMMEDIATELY: Resend lets you send from "onboarding@resend.dev" with
// zero domain verification — sign up, grab an API key, done. No DNS records,
// no waiting. Swap in your own verified domain later if you want a branded
// "from" address; the code doesn't change, only the FromAddress config value.
// ══════════════════════════════════════════════════════════════════════════════

public class ResendOptions
{
    public const string SectionName = "Resend";
    public string ApiKey { get; set; } = string.Empty;          // SECRET — user-secrets / Render env var
    public string FromAddress { get; set; } = "onboarding@resend.dev";
    public string FromName { get; set; } = "Susu Circle";
}

public interface IEmailSender
{
    Task SendEmailAsync(string toAddress, string subject, string htmlBody, CancellationToken ct = default);
}

public class ResendEmailSender(
    HttpClient http,
    IOptions<ResendOptions> options,
    ILogger<ResendEmailSender> logger) : IEmailSender
{
    private readonly ResendOptions _opt = options.Value;

    public async Task SendEmailAsync(string toAddress, string subject, string htmlBody, CancellationToken ct = default)
    {
        // Never let an email failure bubble up and break the calling flow
        // (e.g. member creation). Log and move on — email is best-effort.
        try
        {
            var payload = new
            {
                from = $"{_opt.FromName} <{_opt.FromAddress}>",
                to = new[] { toAddress },
                subject,
                html = htmlBody,
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "/emails")
            {
                Content = JsonContent.Create(payload),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.ApiKey);

            using var resp = await http.SendAsync(req, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                logger.LogError("Resend email failed ({Status}) to {To}: {Body}", resp.StatusCode, toAddress, body);
                return; // swallow — caller's flow must not break
            }

            logger.LogInformation("Email sent to {To} — subject: {Subject}", toAddress, subject);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Email send threw for {To} — subject: {Subject}", toAddress, subject);
            // swallow — never let email delivery break a business flow
        }
    }
}

// ── DI registration ──────────────────────────────────────────────────────────
public static class ResendServiceExtensions
{
    // Call from Program.cs:  builder.Services.AddResendEmail(builder.Configuration);
    public static IServiceCollection AddResendEmail(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ResendOptions>(config.GetSection(ResendOptions.SectionName));

        services.AddHttpClient<IEmailSender, ResendEmailSender>(http =>
        {
            http.BaseAddress = new Uri("https://api.resend.com");
            http.Timeout = TimeSpan.FromSeconds(15);
        });

        return services;
    }
}