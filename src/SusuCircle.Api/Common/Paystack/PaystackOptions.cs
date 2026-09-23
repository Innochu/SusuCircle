namespace SusuCircle.Api.Common.Paystack;

// Strongly-typed Paystack config. Bind from configuration section "Paystack".
// SecretKey is a SECRET — set via user-secrets / env vars, never committed.
//
// Why this exists alongside NombaOptions: Paystack issues TEST keys immediately
// on signup with no business verification, and dedicated virtual accounts work
// in test mode against "test-bank". That makes the whole collect → reconcile →
// payout loop demonstrable while a live merchant account is still being
// approved — which Nomba's sandbox does not currently allow.
public class PaystackOptions
{
    public const string SectionName = "Paystack";

    // Paystack uses ONE host for both modes; test vs live is decided purely by
    // which secret key you send. There is no separate sandbox host.
    public string BaseUrl { get; set; } = "https://api.paystack.co";

    // sk_test_… or sk_live_…. SECRET. Also doubles as the webhook signing key —
    // Paystack signs webhooks with the same secret key, so there is no separate
    // webhook secret to configure.
    public string SecretKey { get; set; } = string.Empty;

    // Bank that backs the dedicated virtual account. Paystack requires
    // "test-bank" in test mode; live mode uses "wema-bank" or "titan-bank".
    // Left unset, it is derived from the key prefix (see ResolvedPreferredBank),
    // which keeps the two from drifting out of sync.
    public string PreferredBank { get; set; } = string.Empty;

    // ── Derived ────────────────────────────────────────────────────────────────

    // A test key is the only thing that makes this test mode.
    public bool IsTestMode =>
        SecretKey.StartsWith("sk_test_", StringComparison.OrdinalIgnoreCase);

    // Picking the bank from the key prefix prevents the single most common
    // Paystack DVA failure: sending preferred_bank "wema-bank" with a test key
    // (or "test-bank" with a live key), which Paystack rejects outright.
    public string ResolvedPreferredBank =>
        !string.IsNullOrWhiteSpace(PreferredBank)
            ? PreferredBank
            : IsTestMode ? "test-bank" : "wema-bank";

    public bool IsConfigured => MissingSettings().Count == 0;

    // Names of config keys that still need a real value, so startup can fail with
    // a message that says exactly what to set rather than a bare 401 from Paystack.
    public IReadOnlyList<string> MissingSettings()
    {
        var missing = new List<string>();
        if (!IsConfiguredValue(BaseUrl)) missing.Add("Paystack:BaseUrl");
        if (!IsConfiguredValue(SecretKey)) missing.Add("Paystack:SecretKey");
        return missing;
    }

    // Treats the shipped "REPLACE_WITH_..." placeholder as not configured — it is
    // present in appsettings.json, so a plain null/empty check would pass it
    // straight through to Paystack as if it were a real key.
    private static bool IsConfiguredValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.StartsWith("REPLACE_WITH", StringComparison.OrdinalIgnoreCase);
}
