using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace SusuCircle.Api.Common.Services;

// ══════════════════════════════════════════════════════════════════════════════
// ALTERNATIVE to ResendEmailSender/GmailSmtpEmailSender — same IEmailSender
// interface, drop-in swap. Built against Brevo's real API (verified against
// developers.brevo.com/docs/send-a-transactional-email):
//   POST https://api.brevo.com/v3/smtp/email
//   Header: api-key: <key>  (NOT "Authorization: Bearer" — Brevo's own scheme)
//   Body: { sender: {name, email}, to: [{email, name}], subject, htmlContent }
//
// WIRED TO YOUR EXISTING "SmtpSettings" CONFIG BLOCK — no appsettings.json
// restructuring needed. It reads:
//   BREVO_API_KEY  -> ApiKey    (the HTTP API key, NOT the raw SMTP password)
//   FromEmail      -> sender email (must be verified in Brevo's Senders page)
//   FromName       -> sender display name
//
// NOTE: Host, Port, Username, Password, EnableSsl, UseStartTls in your
// SmtpSettings block are for Brevo's SMTP-RELAY method — unused by this HTTP
// API approach. Safe to leave them there (harmless dead config) or remove
// once this is confirmed working; nothing here reads them.
//
// SECRET HANDLING: BREVO_API_KEY must be set via user-secrets locally / a
// Render env var in production — never a real value committed in
// appsettings.json, same rule as every other secret tonight.
//
// SETUP:
//   1. Sign up at brevo.com.
//   2. Add + verify a sender at app.brevo.com > Senders (email-confirmation
//      click, no domain required to start sending).
//   3. Get your API key at app.brevo.com/settings/keys/api.
//   4. Register in Program.cs — only ONE IEmailSender should be active:
//        builder.Services.AddBrevoEmail(builder.Configuration);
// ══════════════════════════════════════════════════════════════════════════════

public class BrevoOptions
{
    public const string SectionName = "SmtpSettings"; // matches your existing appsettings block

    [ConfigurationKeyName("BREVO_API_KEY")]
    public string ApiKey { get; set; } = string.Empty;   // SECRET

    public string FromEmail { get; set; } = string.Empty; // must be verified in Brevo
    public string FromName { get; set; } = "Susu Circle";
}

public class BrevoEmailSender(HttpClient http, IOptions<BrevoOptions> options, ILogger<BrevoEmailSender> logger)
    : IEmailSender
{
    private readonly BrevoOptions _opt = options.Value;

    public async Task SendEmailAsync(string toAddress, string subject, string htmlBody, CancellationToken ct = default)
    {
        try
        {
            var payload = new
            {
                sender = new { name = _opt.FromName, email = _opt.FromEmail },
                to = new[] { new { email = toAddress } },
                subject,
                htmlContent = htmlBody,
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "/v3/smtp/email")
            {
                Content = JsonContent.Create(payload),
            };
            req.Headers.Add("api-key", _opt.ApiKey);   // Brevo's own auth scheme, not Bearer
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await http.SendAsync(req, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                logger.LogError("Brevo email failed ({Status}) to {To}: {Body}", resp.StatusCode, toAddress, raw);
                return; // swallow — email is best-effort, must never break the caller's flow
            }

            logger.LogInformation("Email sent via Brevo to {To} — subject: {Subject}", toAddress, subject);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Brevo email send threw for {To} — subject: {Subject}", toAddress, subject);
        }
    }
}

public static class BrevoServiceExtensions
{
    public static IServiceCollection AddBrevoEmail(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<BrevoOptions>(config.GetSection(BrevoOptions.SectionName));

        services.AddHttpClient<IEmailSender, BrevoEmailSender>(http =>
        {
            http.BaseAddress = new Uri("https://api.brevo.com");
            http.Timeout = TimeSpan.FromSeconds(15);
        });

        return services;
    }
}