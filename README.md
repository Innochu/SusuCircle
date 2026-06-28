# Susu Circle — Backend

Vertical Slice Architecture · ASP.NET Core 8 · PostgreSQL · MediatR · SignalR

## Quick Start

```bash
# 1. Clone and restore
dotnet restore

# 2. Configure secrets (never commit real secrets)
cp src/SusuCircle.Api/appsettings.json src/SusuCircle.Api/appsettings.Development.json
# Edit appsettings.Development.json with your Nomba sandbox keys and DB string

# 3. Run database migrations
dotnet ef database update --project src/SusuCircle.Api

# 4. Run the API
dotnet run --project src/SusuCircle.Api
# Swagger UI: https://localhost:5001/swagger
# Hangfire:   https://localhost:5001/hangfire
```

## Architecture

Each feature lives in its own slice under `Features/`:

```
Features/
  Auth/
    Login/          ← LoginCommand + LoginHandler + LoginEndpoint + LoginValidator
    RefreshToken/
  Circles/
    CreateCircle/   ← one file per slice, all concerns co-located
    GetCircle/
    ListCircles/
    UpdateCircleStatus/
  Members/
    AddMember/      ← provisions Nomba VA synchronously
    GetMember/
    ListMembers/
    GetMemberPassport/  ← ADASHI plan credit passport
  Contributions/
    GetContributionBoard/  ← live board (admin dashboard)
    GetContributions/      ← member history
  Payouts/
    TriggerPayout/   ← payout engine + cycle advance logic
    GetPayouts/
  Webhooks/
    NombaWebhook/    ← HMAC-verified · core reconciliation engine
  Notifications/
    GetNotifications/
```

## Key Design Decisions

- **Vertical Slice**: each feature folder is fully self-contained (command, handler, validator, endpoint). No shared repositories.
- **Reconciliation is a MediatR handler**: `NombaWebhookHandler` implements the full FRD decision tree — idempotency, partial, overpayment, credit carry-over, payout eligibility check, SignalR push.
- **Nomba VA = reconciliation key**: member lookup is purely by `virtualAccountNumber`. No fragile bank reference parsing.
- **Idempotency**: `NombaTransactionRef` has a unique index. Duplicate webhooks are rejected at the DB level.
- **Background jobs via Hangfire**: default-check, payment reminders, payout retry with exponential back-off.
- **SignalR**: `CircleHub` pushes `ContributionUpdated` events to the admin dashboard group on every reconciled payment.
- **Credit Score (ADASHI)**: recalculated after every contribution event via `ICreditScoreService`.

## Environment Variables

| Key | Description |
|-----|-------------|
| `ConnectionStrings__Default` | PostgreSQL connection string |
| `Jwt__Secret` | Min 32-char JWT signing key |
| `Nomba__ApiKey` | Nomba sandbox API key |
| `Nomba__AccountId` | Nomba account ID |
| `Nomba__WebhookSecret` | Nomba webhook HMAC secret |

## Migrations

```bash
dotnet ef migrations add InitialCreate --project src/SusuCircle.Api
dotnet ef database update --project src/SusuCircle.Api
```
