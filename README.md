# Susu Circle
### A digital platform for rotating savings groups (Ajo / Esusu / Susu)

Vertical Slice Architecture · ASP.NET Core 8 · PostgreSQL · MediatR · SignalR · Hangfire

**Live app:** https://susu-pay.onrender.com

---

## The Problem

Traditional esusu/ajo savings groups run on trust and manual bookkeeping. A coordinator collects cash, tracks who paid in a notebook or WhatsApp thread, and manually disburses payouts. Coordinators can't accurately reconcile who paid, who paid short, or who's overdue; members have no independent proof of payment; trust collapses if the coordinator mismanages funds; and disciplined, consistent savers build no credit history at all, because banks simply can't see informal group activity.

**Susu Circle replaces the manual bookkeeping with automated infrastructure**, while keeping the social structure of esusu intact. Every member gets a dedicated virtual bank account. Every contribution reconciles automatically. Every payout goes to a verified real bank account, not just a database flag. Every member builds a visible, verifiable savings history.

**Development status:** Fully functional MVP — the core loop (create a circle → onboard members with real virtual accounts → collect and reconcile contributions automatically → pay out to a verified bank account) runs end to end against live production infrastructure, proven with genuine bank transfers, not sandbox mocks.

---

## Quick Start

\`\`\`bash
# 1. Clone and restore
dotnet restore

# 2. Configure secrets (never commit real secrets)
cp src/SusuCircle.Api/appsettings.json src/SusuCircle.Api/appsettings.Development.json
# Edit appsettings.Development.json with your DB connection string.
# Nomba credentials, JWT secret, and email provider keys should be set via
# `dotnet user-secrets` locally, or environment variables in production —
# see Environment Variables below. Never commit real values into
# appsettings.*.json.

# 3. Run database migrations
dotnet ef database update --project src/SusuCircle.Api

# 4. Run the API
dotnet run --project src/SusuCircle.Api

# Swagger UI: https://localhost:5001/swagger
# Hangfire:   https://localhost:5001/hangfire
\`\`\`

---

## Architecture

Each feature lives in its own slice under \`Features/\` — command/query, validator, handler, and endpoint co-located in one file, so the entire story of a feature is readable in one place rather than scattered across layered folders. No shared repository layer; MediatR handlers talk to \`AppDbContext\` directly.

\`\`\`
Features/
  Auth/
    Login/              ← unified login — admins AND members, same endpoint
    Register/
    Logout/              ← refresh-token invalidation
    ForgotPassword/       ← emailed one-time code, 15-min expiry
    RefreshToken/

  Circles/
    CreateCircle/         ← BAM or ADASHI
    GetCircle/
    ListCircles/
    UpdateCircleStatus/
    GetCircleByMember/    ← resolve a member's circle from just their memberId

  Members/
    AddMember/             ← provisions Nomba VA synchronously, issues login
                              credentials, emails welcome + credentials
    GetMember/
    ListMembers/
    GetMemberPassport/     ← lifetime contribution history / credit signal
    GetMemberHome/         ← member portal: home tab
    GetMemberContributions/← member portal: contributions tab
    GetMemberPayoutView/   ← member portal: payout tab
    GetMemberNotifications/← member portal: notifications tab, unread badge
    SetPayoutAccount/       ← member self-service: register + verify a real
                              bank account for receiving payouts

  Contributions/
    GetContributionBoard/  ← live reconciliation board (admin dashboard)
    GetContributions/      ← raw per-cycle history

  Reconciliation/
    Match/                 ← manual match for anything auto-reconciliation
                              couldn't resolve (Match Transactions screen)
    Sweep/                 ← manual + scheduled fallback sweep against
                              Nomba's Transactions API

  Payouts/
    TriggerPayout/          ← payout engine + cycle advance logic
    GetPayouts/
    GetPayoutBoard/          ← live queue + history, admin and member views

  Webhooks/
    NombaWebhook/            ← HMAC-verified · the core reconciliation engine

  Notifications/
    GetNotifications/        ← admin-facing feed, unread badge

  Dev/
    SimulateTransfer/        ← sandbox-safe: fire a real Nomba transfer into
                              a member's own VA, for testing reconciliation
    SimulateWebhook/          ← feed a hand-built payload directly into the
                              real reconciliation pipeline, bypassing Nomba
                              delivery entirely — isolates "does our logic
                              work" from "did Nomba deliver anything"
    CheckBalance/

Infrastructure/
  Jobs/
    AutoReconciliationJob/    ← the fallback reconciliation path, see below
\`\`\`

---

## Key Design Decisions

- **Vertical Slice.** Each feature folder is fully self-contained. No shared repositories.
- **Reconciliation is a MediatR handler**, \`NombaWebhookHandler\`, implementing the full decision tree: idempotency check, Paid/Partial/Overpaid classification, credit carry-over to the next cycle, payout-eligibility check, SignalR push, admin + member notifications.
- **Member identity is the reconciliation key, matched precisely** — not a fuzzy field guess. Nomba's transaction records expose \`virtualAccountReference\`, which is literally the \`Member.Id\` used as the account reference at VA-creation time — the most precise possible match, confirmed against real transaction payloads rather than assumed from documentation. \`recipientAccountNumber\` (the VA that received funds) is the fallback match.
- **Idempotency at the database level.** \`Contribution.NombaTransactionRef\` carries a unique index; duplicate webhook deliveries — Nomba explicitly warns these happen — are rejected before they can double-apply a payment.
- **Two independent reconciliation paths, not one.** See below — this is the single most important resilience decision in the system.
- **Payouts go to a verified real bank account, never the member's own contribution VA.** Because any transfer into a Nomba virtual account is automatically re-pooled into the merchant wallet, paying a member's payout into their own VA would never actually reach them. Members register and verify a separate real bank account for receiving payouts, via \`SetPayoutAccount\`, with the account name resolved live against Nomba before it's ever saved.
- **Background jobs via Hangfire** — the reconciliation fallback sweep, payment reminders, payout retry — all wrapped defensively so a transient failure (e.g. a distributed-lock timeout during a redeploy) never crashes the whole application; a job persists in Hangfire's own storage independent of any single registration attempt.
- **SignalR** — \`CircleHub\` pushes \`ContributionUpdated\` events to the admin dashboard group on every reconciled payment.
- **Credit Score (ADASHI)** — recalculated after every contribution event via \`ICreditScoreService\`.

---

## The Reconciliation Engine, In Detail

### The happy path

1. A member transfers money into their dedicated virtual account.
2. Nomba reports the transaction — either via a real-time webhook, or discovered by the fallback sweep (below).
3. The engine matches the transaction to the member by their virtual account identity.
4. It finds the member's open contribution for the current cycle and compares received vs. expected:
   - **Exact match** → **Paid**
   - **Short** → **Partial** — member notified of the exact remaining balance and deadline
   - **Over** → **Overpaid** — the excess is automatically credited toward the member's *next* cycle, not just refunded and forgotten
5. Member and admin are both notified in real time; the admin dashboard updates live via SignalR.
6. Once the cycle's collection threshold is met (per the circle's plan rules — see below), a payout is automatically queued.

### BAM vs. ADASHI payout eligibility

- **BAM** — payout releases once *every* active member's contribution for the cycle is fully settled.
- **ADASHI** — payout releases once the *total collected* meets the expected pool total, even if individual contributions arrived unevenly (an overpayment can cover another member's shortfall within the same cycle).

### Built-in resilience: why there are two independent reconciliation paths

Midway through development, Nomba's sandbox webhook delivery turned out to be broken platform-wide — confirmed directly by Nomba's own team, affecting multiple teams building against their API concurrently. Rather than treat that as a blocker to wait out, it became a deliberate design decision:

- **Path 1 — real-time webhooks.** Nomba pushes a \`payment_success\` event the moment money lands in a member's VA. Verified with HMAC-SHA256 signature checking (constant-time comparison) before any processing occurs.
- **Path 2 — scheduled reconciliation sweep** (\`AutoReconciliationJob\`, running every few minutes via Hangfire). Polls Nomba's own Transactions API directly, entirely independent of whether any webhook was ever delivered. It identifies genuine inbound payments by matching the transaction's \`virtualAccountReference\` to a member's identity, then feeds the discovery through the **exact same reconciliation pipeline** a live webhook would use — same balance math, same notifications, same payout-triggering logic, achieved by constructing an equivalent internal command rather than duplicating any reconciliation logic.

Both paths have been independently confirmed working against real, live transactions in production. A payment is never missed just because one delivery mechanism had an outage.

---

## Environment Variables

**Never commit real secret values.** Use \`dotnet user-secrets\` locally and platform environment variables (e.g. Render's Environment tab) in deployment. Double underscore (\`__\`) maps to nested config sections.

| Key | Description |
|---|---|
| \`ConnectionStrings__Default\` | PostgreSQL connection string |
| \`Jwt__Secret\` | Min 32-char JWT signing key |
| \`Jwt__Issuer\` / \`Jwt__Audience\` | JWT validation parameters |
| \`Jwt__ExpiryMinutes\` | Access token lifetime |
| \`Nomba__BaseUrl\` | \`https://sandbox.nomba.com\` or \`https://api.nomba.com\` — this alone controls sandbox vs. live |
| \`Nomba__ParentAccountId\` | Sent in the \`accountId\` header on every Nomba request (always the parent, never the sub-account) |
| \`Nomba__SubAccountId\` | Used to scope transfer/payout calls |
| \`Nomba__ClientId\` / \`Nomba__ClientSecret\` | OAuth client-credentials for token issuance |
| \`Nomba__WebhookSecret\` | HMAC key for verifying inbound webhook signatures |
| \`Nomba__DefaultBankCode\` | Bank code Nomba virtual accounts are issued under |
| \`SmtpSettings__BREVO_API_KEY\` | Brevo transactional email API key |
| \`SmtpSettings__FromEmail\` | Must be a **verified sender** in Brevo's dashboard, or sends fail |
| \`SmtpSettings__FromName\` | Display name on outbound email |
| \`Cors__Origins\` | Allowed frontend origin(s) |

---

## Migrations

\`\`\`bash
dotnet ef migrations add InitialCreate --project src/SusuCircle.Api
dotnet ef database update --project src/SusuCircle.Api
\`\`\`

---

## API Reference (selected)

Full interactive reference: \`https://susucircle.onrender.com/swagger\`

| Area | Endpoint | Purpose |
|---|---|---|
| Auth | \`POST /api/auth/register\` | Admin registration |
| Auth | \`POST /api/auth/login\` | Unified login — checks Admins, then Members |
| Auth | \`POST /api/auth/forgot-password\` → \`reset-password\` | Password recovery via emailed 6-digit code |
| Auth | \`POST /api/auth/logout\` | Refresh-token invalidation |
| Circles | \`POST /api/circles\` | Create a circle (BAM or ADASHI) |
| Members | \`POST /api/circles/{circleId}/members\` | Add a member — provisions Nomba VA, issues credentials |
| Members | \`POST /api/members/{memberId}/payout-account\` | Self-service: register + verify a real payout bank account |
| Members | \`GET /api/members/{memberId}/home\` \`/contributions/summary\` \`/payout\` \`/notification-center\` | Member portal data |
| Reconciliation | \`GET /api/admin/{adminId}/circles/{circleId}/reconciliation\` | Live per-member contribution board |
| Reconciliation | \`POST /api/admin/{adminId}/reconciliation/match\` | Manual match for unresolved transactions |
| Reconciliation | \`POST /api/admin/{adminId}/circles/{circleId}/reconciliation/sweep\` | Manually trigger the Transactions-API fallback sweep |
| Payouts | \`POST /circles/{circleId}/payout\` | Trigger the current cycle's payout |
| Payouts | \`GET /circles/{circleId}/payout-board\` | Live payout queue and history |
| Webhooks | \`POST /api/webhooks/nomba\` | Nomba payment/payout event receiver (HMAC-verified) |
| Reports | \`GET /api/admin/{adminId}/reports\` \`/reports/export\` | Analytics and CSV export |

---

## Judging Criteria Mapping

| Criterion | Where it's addressed |
|---|---|
| **Reconciliation logic quality** | Dual-path engine (real-time webhook + independent scheduled fallback), DB-level idempotency, proven against real live transactions in production |
| **Underpayment / overpayment handling** | Explicit Partial/Overpaid states with exact shortfall notification and automatic credit carry-forward to the next cycle |
| **Customer-level reporting clarity** | Personal contribution history, on-time payment rate, per-cycle timeline, surfaced in the member portal and via a dedicated passport endpoint |

---

## Team

Built during the Nomba Hackathon 2026 by **Team PayGrid**.
