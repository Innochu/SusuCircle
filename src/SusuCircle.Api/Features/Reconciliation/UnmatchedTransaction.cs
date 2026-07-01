namespace SusuCircle.Api.Common.Models;

// ── Entity ────────────────────────────────────────────────────────────────────
// An inbound Nomba transfer the webhook could NOT auto-reconcile. These surface
// on the "Match Transactions" screen for an admin to resolve manually.
//
// Add to AppDbContext:
//     public DbSet<UnmatchedTransaction> UnmatchedTransactions => Set<UnmatchedTransaction>();
//
// Then create a migration:
//     Add-Migration UnmatchedTransactions   (PMC)
//     dotnet ef migrations add UnmatchedTransactions   (CLI)

public class UnmatchedTransaction
{
    public Guid Id { get; set; }

    // The circle this VA belongs to, if it could be resolved that far. If the VA
    // itself was unknown, set this from the admin context when displaying, or leave
    // it linked once a partial match (circle known, contribution not) is found.
    public Guid CircleId { get; set; }

    public decimal Amount { get; set; }
    public string? SenderName { get; set; }
    public string? SenderAccountNumber { get; set; }
    public string? VirtualAccountNumber { get; set; }
    public string TransactionReference { get; set; } = default!;
    public DateTime ReceivedAt { get; set; }

    public bool IsResolved { get; set; }
    public Guid? MatchedMemberId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
