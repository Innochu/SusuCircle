using System.Text;
using MediatR;
using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Exceptions;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence;

namespace SusuCircle.Api.Features.Members.GetMember;

// ── Request ───────────────────────────────────────────────────────────────────

public record ExportReportsQuery(Guid AdminId, Guid? CircleId = null) : IRequest<CsvExportResult>;

public record CsvExportResult(byte[] Bytes, string FileName);

// ── Handler ───────────────────────────────────────────────────────────────────

public class ExportReportsHandler(AppDbContext db) : IRequestHandler<ExportReportsQuery, CsvExportResult>
{
    public async Task<CsvExportResult> Handle(ExportReportsQuery q, CancellationToken ct)
    {
        _ = await db.Admins.FindAsync([q.AdminId], ct)
            ?? throw new NotFoundException(nameof(Admin), q.AdminId);

        var query = db.Contributions
            .Include(c => c.Member)
            .Include(c => c.Circle)
            .Where(c => c.Circle.AdminId == q.AdminId);

        if (q.CircleId.HasValue)
            query = query.Where(c => c.CircleId == q.CircleId.Value);

        var contributions = await query
            .OrderBy(c => c.Circle.Name)
            .ThenBy(c => c.CycleNumber)
            .ThenBy(c => c.Member.PayoutPosition)
            .ToListAsync(ct);

        var csv = BuildCsv(contributions);
        var bytes = Encoding.UTF8.GetBytes(csv);

        var fileName = q.CircleId.HasValue
            ? $"susucircle-contributions-{q.CircleId}-{DateTime.UtcNow:yyyyMMdd}.csv"
            : $"susucircle-all-contributions-{DateTime.UtcNow:yyyyMMdd}.csv";

        return new CsvExportResult(bytes, fileName);
    }

    private static string BuildCsv(List<Contribution> contributions)
    {
        var sb = new StringBuilder();

        // Header row
        sb.AppendLine("Circle,Plan,Cycle,Member Name,Phone,Virtual Account,Expected (NGN),Paid (NGN),Balance (NGN),Credit Applied (NGN),Status,Due Date,Paid At");

        foreach (var c in contributions)
        {
            var fields = new[]
            {
                Escape(c.Circle.Name),
                c.Circle.Plan.ToString(),
                c.CycleNumber.ToString(),
                Escape(c.Member.Name),
                c.Member.Phone,
                c.Member.VirtualAccountNumber ?? "",
                c.ExpectedAmount.ToString("F2"),
                c.PaidAmount.ToString("F2"),
                (c.ExpectedAmount - c.CreditApplied - c.PaidAmount).ToString("F2"),
                c.CreditApplied.ToString("F2"),
                c.Status.ToString(),
                c.DueDate.ToString("yyyy-MM-dd"),
                c.PaidAt?.ToString("yyyy-MM-dd HH:mm") ?? ""
            };

            sb.AppendLine(string.Join(",", fields));
        }

        return sb.ToString();
    }

    // Wrap in quotes and escape internal quotes
    private static string Escape(string value) =>
        $"\"{value.Replace("\"", "\"\"")}\"";
}

// ── Endpoint ──────────────────────────────────────────────────────────────────

public static class ExportReportsEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/admin/{adminId:guid}/reports/export",
            async (Guid adminId, IMediator mediator, Guid? circleId = null) =>
            {
                var result = await mediator.Send(new ExportReportsQuery(adminId, circleId));
                return Results.File(result.Bytes, "text/csv", result.FileName);
            })
        .WithName("ExportReports")
        .WithTags("Admin")
        .AllowAnonymous();
}
