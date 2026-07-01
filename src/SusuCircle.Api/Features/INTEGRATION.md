# Wiring guide

Six new files. Below is exactly where each goes, the DbContext + Program.cs
registrations, and the edits to your **existing** `NombaWebhookHandler` and
`AddMemberHandler` so the reconciliation board and notifications actually populate.

## 1. File placement

```
Features/Circles/GetCircleMembers/GetCircleMembersHandler.cs
Features/Reconciliation/GetReconciliationBoardHandler.cs
Features/Reconciliation/MatchTransactionsHandler.cs
Common/Models/UnmatchedTransaction.cs          (from Reconciliation/UnmatchedTransaction.cs)
Common/Models/AdminNotification.cs             (from Notifications/AdminNotification.cs)
Features/Notifications/Admin/GetAdminNotificationsHandler.cs
Common/Services/AdminNotifier.cs               (from Notifications/AdminNotifier.cs)
```

(The two entity files declare `namespace SusuCircle.Api.Common.Models` already, so
physically drop them under `Common/Models/` regardless of the download folder.)

## 2. AppDbContext — add two DbSets

```csharp
public DbSet<UnmatchedTransaction> UnmatchedTransactions => Set<UnmatchedTransaction>();
public DbSet<AdminNotification> AdminNotifications => Set<AdminNotification>();
```

## 3. ServiceExtensions.AddAppServices() — register the notifier

```csharp
services.AddScoped<IAdminNotifier, AdminNotifier>();
```

## 4. Program.cs — map endpoints + usings

```csharp
using SusuCircle.Api.Features.Circles.GetCircleMembers;
using SusuCircle.Api.Features.Reconciliation.GetBoard;
using SusuCircle.Api.Features.Reconciliation.Match;
using SusuCircle.Api.Features.Notifications.Admin;
```

```csharp
GetCircleMembersEndpoint.Map(app);
GetReconciliationBoardEndpoint.Map(app);
MatchTransactionEndpoints.Map(app);
AdminNotificationEndpoints.Map(app);
```

## 5. Migration

PMC:      `Add-Migration ReconciliationAndAdminNotifications` then `Update-Database`
CLI:      `dotnet ef migrations add ReconciliationAndAdminNotifications` then `dotnet ef database update`

## 6. SignalR — join the admin group (live badge)

`CircleHub` currently groups by circleId. Add an admin group so `AdminNotifier`
can push to `admin-{adminId}`. In `CircleHub`:

```csharp
public async Task JoinAdminGroup(string adminId) =>
    await Groups.AddToGroupAsync(Context.ConnectionId, $"admin-{adminId}");
```

Frontend calls `hubConnection.invoke("JoinAdminGroup", adminId)` after connect,
and listens with `hubConnection.on("AdminNotification", n => ...)`.

---

## 7. Make the webhook write admin notifications  (EXISTING FILE EDIT)

In `NombaWebhookHandler`, inject the notifier:

```csharp
public class NombaWebhookHandler(
    AppDbContext db,
    INotificationService notifications,
    IAdminNotifier adminNotifier,          // ← add
    IHubContext<CircleHub> hub,
    ICreditScoreService creditScore,
    ILogger<NombaWebhookHandler> logger)
```

Then in `Handle`, right after `await db.SaveChangesAsync(ct);` (after the
Paid/Partial/Overpaid block), add the admin-side notification:

```csharp
var adminId = circle.AdminId;   // Circle already loaded via member.Circle
var evtType = contribution.Status switch
{
    ContributionStatus.Partial => AdminNotificationType.PartialPayment,
    _ => AdminNotificationType.PaymentReceived,
};
var partialSuffix = contribution.Status == ContributionStatus.Partial
    ? $" (₦{balance:N0} outstanding)" : "";

await adminNotifier.NotifyAsync(
    adminId,
    evtType,
    contribution.Status == ContributionStatus.Partial
        ? "Partial payment received"
        : "Payment received",
    $"{member.Name} paid ₦{payload.Amount:N0} · {circle.Name} — Cycle {circle.CurrentCycle}{partialSuffix}",
    circle.Id,
    circle.Name,
    ct);
```

For the **unmatched** path (the early `return` when member or contribution is
null): instead of just returning, persist an `UnmatchedTransaction` so it shows
on the Match screen. Replace the two early returns for
"Account number not found" and "No open contribution" with:

```csharp
db.UnmatchedTransactions.Add(new UnmatchedTransaction
{
    Id = Guid.NewGuid(),
    CircleId = member?.CircleId ?? Guid.Empty,   // Empty when VA unknown
    Amount = payload.Amount,
    SenderName = payload.SenderName,             // map from your payload
    SenderAccountNumber = payload.SenderAccountNumber,
    VirtualAccountNumber = payload.AccountNumber,
    TransactionReference = payload.TransactionReference,
    ReceivedAt = payload.Timestamp,
});
await db.SaveChangesAsync(ct);
return new WebhookResult(false, "Unmatched — queued for manual reconciliation.");
```

(If your `NombaWebhookPayload` has no SenderName/SenderAccountNumber fields, add
them or set null — image 3 shows sender name + account, so map them if Nomba
provides them.)

## 8. Payout notifications  (EXISTING FILE EDIT)

In `CheckAndTriggerPayoutAsync`, after `db.Payouts.Add(...)` and the log line:

```csharp
await adminNotifier.NotifyAsync(
    circle.AdminId,
    AdminNotificationType.PayoutTriggered,
    "Payout queued",
    $"{payoutMember.Name} is up for payout · {circle.Name} — Cycle {circle.CurrentCycle}",
    circle.Id, circle.Name, ct);
```

## 9. New-member notification  (EXISTING FILE EDIT — AddMemberHandler)

After you save the new member:

```csharp
await adminNotifier.NotifyAsync(
    circle.AdminId,
    AdminNotificationType.NewMemberJoined,
    "New member joined",
    $"{member.Name} joined {circle.Name}",
    circle.Id, circle.Name, ct);
```

(Inject `IAdminNotifier adminNotifier` into `AddMemberHandler` the same way.)

## 10. Overdue notifications  (BACKGROUND JOB)

The "5 days overdue" rows come from a scheduled sweep, not an inbound event. In
your existing Hangfire job area add a daily job that finds contributions past
`DueDate` still owing, and for each (throttled to once) calls:

```csharp
await adminNotifier.NotifyAsync(
    circle.AdminId,
    AdminNotificationType.PaymentOverdue,
    "Payment overdue",
    $"{member.Name}'s payment for {circle.Name} is {daysOverdue} days overdue",
    circle.Id, circle.Name, ct);
```

Guard against duplicates (e.g. only notify once per member per cycle) by checking
for an existing PaymentOverdue notification with the same body, or track a flag.
