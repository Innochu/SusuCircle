namespace SusuCircle.Api.Common.Nomba;

// Strongly-typed Nomba config. Bind from configuration section "Nomba".
// ClientSecret and WebhookSecret are SECRETS — set via user-secrets / env vars,
// never committed in appsettings.json.
public class NombaOptions
{
    public const string SectionName = "Nomba";

    // https://api.nomba.com (live) or https://sandbox.nomba.com (test)
    public string BaseUrl { get; set; } = "https://sandbox.nomba.com";

    // Parent (main) account ID — sent in the `accountId` header on EVERY request.
    public string ParentAccountId { get; set; } = string.Empty;

    // Your sub-account ID — used to scope sub-account transfers (optional).
    public string SubAccountId { get; set; } = string.Empty;

    // OAuth client_credentials. ClientSecret is SECRET.
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    // Shared secret for verifying inbound webhook HMAC-SHA256 signatures. SECRET.
    // Binds from config key "Nomba:WebhookSecret" (matches existing webhook code).
    public string WebhookSecret { get; set; } = string.Empty;

    // Bank code used for Nomba virtual accounts / member payouts.
    public string DefaultBankCode { get; set; } = "000026";
}