namespace SusuCircle.Api.Common.Nomba;

// Strongly-typed Nomba config. Bind from configuration section "Nomba".
// ClientSecret/PrivateKey and WebhookSecret are SECRETS — set via user-secrets /
// env vars, never committed in appsettings.json.
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

    // Nomba's dashboard labels the credential pair "Client ID" and "Private Key",
    // so configs are commonly written with a `PrivateKey` key. The token endpoint
    // takes that private key as `client_secret` — both names bind to the same
    // credential here (see ResolvedClientSecret). Without this alias a config
    // that only sets Nomba:PrivateKey issues token requests with an EMPTY
    // client_secret, every call 401s, and virtual account creation fails.
    public string PrivateKey { get; set; } = string.Empty;

    // Legacy key names from the original appsettings template, kept so an older
    // config keeps working. ApiKey is treated as the client secret; AccountId as
    // the parent account ID.
    public string ApiKey { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;

    // Shared secret for verifying inbound webhook HMAC-SHA256 signatures. SECRET.
    // Binds from config key "Nomba:WebhookSecret" (matches existing webhook code).
    public string WebhookSecret { get; set; } = string.Empty;

    // Bank code used for Nomba virtual accounts / member payouts.
    public string DefaultBankCode { get; set; } = "000026";

    // Nomba's sandbox is open. Its docs state: "No sign-up needed. Call the
    // sandbox endpoints below directly — skip the Authorization header and
    // accountId entirely." With this on, requests go out unauthenticated, which
    // yields real Nomba sandbox virtual accounts with no credentials at all —
    // useful while live/test credentials are unavailable.
    //
    // Only ever honoured against the sandbox host (see UnauthenticatedSandboxActive),
    // so this can never cause an unauthenticated call to the live API no matter
    // how the flag is set.
    public bool UseUnauthenticatedSandbox { get; set; }

    public bool IsSandboxHost =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) &&
        uri.Host.Equals("sandbox.nomba.com", StringComparison.OrdinalIgnoreCase);

    public bool UnauthenticatedSandboxActive => UseUnauthenticatedSandbox && IsSandboxHost;

    // ── Resolved credentials — always read these, never the raw properties ──────

    public string ResolvedClientSecret => FirstConfigured(ClientSecret, PrivateKey, ApiKey);
    public string ResolvedAccountId => FirstConfigured(ParentAccountId, AccountId);
    public string ResolvedClientId => FirstConfigured(ClientId);

    public bool IsConfigured => MissingSettings().Count == 0;

    // Names of the config keys that still need a real value. Used to fail with a
    // message that says exactly what to set instead of a bare 401 from Nomba.
    public IReadOnlyList<string> MissingSettings()
    {
        var missing = new List<string>();
        if (!IsConfiguredValue(BaseUrl)) missing.Add("Nomba:BaseUrl");

        // The open sandbox needs no credentials, so nothing further is required.
        if (UnauthenticatedSandboxActive) return missing;

        if (!IsConfiguredValue(ResolvedClientId)) missing.Add("Nomba:ClientId");
        if (!IsConfiguredValue(ResolvedClientSecret)) missing.Add("Nomba:ClientSecret (or Nomba:PrivateKey)");
        if (!IsConfiguredValue(ResolvedAccountId)) missing.Add("Nomba:ParentAccountId (or Nomba:AccountId)");
        return missing;
    }

    private static string FirstConfigured(params string[] values) =>
        Array.Find(values, IsConfiguredValue) ?? string.Empty;

    // Treats the shipped "REPLACE_WITH_..." placeholders as not configured — they
    // are present in appsettings.json, so a plain null/empty check passes them
    // straight through to Nomba as if they were real credentials.
    private static bool IsConfiguredValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.StartsWith("REPLACE_WITH", StringComparison.OrdinalIgnoreCase);
}
