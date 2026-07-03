using Hangfire;
using Microsoft.OpenApi.Models;
using Serilog;
using SusuCircle.Api.Common.Extensions;
using SusuCircle.Api.Common.Middleware;
using SusuCircle.Api.Common.Nomba;
using SusuCircle.Api.Common.Services;
using SusuCircle.Api.Features.Auth.Login;
using SusuCircle.Api.Features.Auth.RefreshToken;
using SusuCircle.Api.Features.Auth.Register;
using SusuCircle.Api.Features.Circles.CreateCircle;
using SusuCircle.Api.Features.Circles.GetCircle;
using SusuCircle.Api.Features.Circles.ListCircles;
using SusuCircle.Api.Features.Circles.UpdateCircleStatus;
using SusuCircle.Api.Features.Contributions.GetContributionBoard;
using SusuCircle.Api.Features.Contributions.GetContributions;
using SusuCircle.Api.Features.Dev.CheckBalance;
using SusuCircle.Api.Features.Dev.SimulateTransfer;
using SusuCircle.Api.Features.Dev.SimulateWebhook;
using SusuCircle.Api.Features.Members.AddMember;
using SusuCircle.Api.Features.Members.GetMember;
using SusuCircle.Api.Features.Members.GetMemberPassport;
using SusuCircle.Api.Features.Members.ListMembers;
using SusuCircle.Api.Features.Notifications;
using SusuCircle.Api.Features.Notifications.GetNotifications;
using SusuCircle.Api.Features.Payouts.GetPayoutBoard;
using SusuCircle.Api.Features.Payouts.GetPayouts;
using SusuCircle.Api.Features.Payouts.TriggerPayout;
using SusuCircle.Api.Features.Reconciliation.Match;
using SusuCircle.Api.Features.Reconciliation.sweep;
using SusuCircle.Api.Features.Webhooks.NombaWebhook;
using SusuCircle.Api.Infrastructure;
// ── Bootstrap ─────────────────────────────────────────────────────────────────

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .WriteTo.Console());

// ── Services ─────────────────────────────────────────────────────────────────
builder.Services
    .AddDatabase(builder.Configuration)
    .AddJwtAuth(builder.Configuration)
    .AddMediator()
    .AddValidators()
    .AddAppServices()
    .AddBackgroundJobs(builder.Configuration)
     .AddResendEmail(builder.Configuration)
    .AddCircleHub()
    .AddAntiforgery()
    .AddNombaClient(builder.Configuration)
    .AddEndpointsApiExplorer()
    .AddScoped<AutoReconciliationJob>()
    .AddSwaggerGen()
    .AddCors(opt => opt.AddDefaultPolicy(p => p
        .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173"])
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IAdminNotifier, AdminNotifier>();
// ── Pipeline ──────────────────────────────────────────────────────────────────

var app = builder.Build();

app.UseMiddleware<ExceptionMiddleware>();
app.UseSerilogRequestLogging();
app.UseCors();

if (app.Environment.IsDevelopment() || app.Environment.IsProduction())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// ── Hangfire dashboard (protected in production) ───────────────────────────────

app.UseHangfireDashboard("/hangfire");

// ── SignalR hub ───────────────────────────────────────────────────────────────

app.MapHub<CircleHub>("/hubs/circle");

// ── Route registration (Vertical Slice Endpoints) ────────────────────────────

LoginEndpoint.Map(app);
RefreshTokenEndpoint.Map(app);
RegisterEndpoint.Map(app);
CreateCircleEndpoint.Map(app);
GetCircleEndpoint.Map(app);
ListCirclesEndpoint.Map(app);
UpdateCircleStatusEndpoint.Map(app);
GetAllMembersEndpoint.Map(app);
ExportReportsEndpoint.Map(app);
GetOverviewEndpoint.Map(app);
AddMemberEndpoint.Map(app);
GetMemberEndpoint.Map(app);
ListMembersEndpoint.Map(app);
GetMemberPassportEndpoint.Map(app);
GetReportsEndpoint.Map(app);
GetContributionBoardEndpoint.Map(app);
GetContributionsEndpoint.Map(app);
MemberDashboardEndpoint.Map(app);
TriggerPayoutEndpoint.Map(app);
GetPayoutsEndpoint.Map(app);
NotificationEndpoints.Map(app);
NombaWebhookEndpoint.Map(app);
AdminNotificationEndpoints.Map(app);
MatchTransactionEndpoints.Map(app);
GetPayoutBoardEndpoint.Map(app);
SimulateTransferEndpoint.Map(app);
SimulateWebhookEndpoint.Map(app);
RunReconciliationSweepEndpoint.Map(app);
CheckBalanceEndpoint.Map(app);
// ── Hangfire recurring jobs ───────────────────────────────────────────────────

RecurringJob.AddOrUpdate<DefaultCheckJob>(
    "default-check",
    job => job.RunAsync(),
    Cron.Daily);

RecurringJob.AddOrUpdate<PaymentReminderJob>(
    "payment-reminders",
    job => job.RunAsync(),
    Cron.Daily);

RecurringJob.AddOrUpdate<PayoutRetryJob>(
    "payout-retry",
    job => job.RunAsync(),
    "0 */4 * * *"); // Every 4 hours

RecurringJob.AddOrUpdate<AutoReconciliationJob>(
    "auto-reconciliation-sweep",
    job => job.RunAsync(default),
    "*/1 * * * *"); // every 1 minute   
// ── Run ───────────────────────────────────────────────────────────────────────

app.Run();

public partial class Program { }
