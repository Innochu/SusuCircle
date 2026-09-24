namespace SusuCircle.Api.Common.Models;

// ── Enums ─────────────────────────────────────────────────────────────────────

public enum PlanType { BAM, ADASHI }

public enum CircleStatus { Setup, Active, Paused, Completed }

public enum ContributionFrequency { Weekly, Biweekly, Monthly }

public enum PayoutOrderType { Sequential, Random, Bidding }

public enum MemberStatus { Active, Suspended, Completed }

public enum ContributionStatus { Pending, Partial, Paid, Overpaid, Defaulted, Overdue, Unpaid }

public enum PayoutStatus { Pending, Processing, Completed, Failed }

public enum NotificationType { PaymentReceived, PaymentReminder, PayoutSent, MemberAdded, CirclePaused, Defaulted }

// ── Domain Entities ───────────────────────────────────────────────────────────

public class Admin
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
    public DateTime? RefreshTokenExpiry { get; set; }
    public DateTime? ResetCodeExpiry { get; set; }
    public string? ResetCode { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<Circle> Circles { get; set; } = new List<Circle>();
}

public class Circle
{
    public Guid Id { get; set; }
    public Guid AdminId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public PlanType Plan { get; set; }
    public decimal ContributionAmount { get; set; }
    public ContributionFrequency Frequency { get; set; }
    public int MaxMembers { get; set; }
    public int CurrentCycle { get; set; } = 1;
    public CircleStatus Status { get; set; } = CircleStatus.Setup;
    public PayoutOrderType PayoutOrder { get; set; } = PayoutOrderType.Sequential;
    public DateTime StartDate { get; set; }
    public DateTime NextContributionDate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Admin Admin { get; set; } = null!;
    public ICollection<Member> Members { get; set; } = new List<Member>();
    public ICollection<Contribution> Contributions { get; set; } = new List<Contribution>();
    public ICollection<Payout> Payouts { get; set; } = new List<Payout>();
    public ICollection<CollectionAccount> CollectionAccounts { get; set; } = new List<CollectionAccount>();
}

public class Member
{
    public Guid Id { get; set; }
    public Guid CircleId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public int PayoutPosition { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime? ResetCodeExpiry { get; set; }
    public string? ResetCode { get; set; }
    public DateTime? RefreshTokenExpiry { get; set; }
    public string? VirtualAccountId { get; set; }
    public string? VirtualAccountNumber { get; set; }
    public string? BankName { get; set; }
    public MemberStatus Status { get; set; } = MemberStatus.Active;
    public int CreditScore { get; set; } = 50;
    public string? PayoutBankAccountNumber { get; set; }
    public string? PayoutBankCode { get; set; }
    public string? PayoutBankName { get; set; }
    public string? PayoutBankLabel { get; set; }
    public string CreditTier { get; set; } = "Fair";
    public int ConsecutiveOnTimeStreak { get; set; } = 0;
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;

    public Circle Circle { get; set; } = null!;
    public ICollection<Contribution> Contributions { get; set; } = new List<Contribution>();
    public ICollection<Payout> Payouts { get; set; } = new List<Payout>();
    public ICollection<Notification> Notifications { get; set; } = new List<Notification>();
}

public class Contribution
{
    public Guid Id { get; set; }
    public Guid MemberId { get; set; }
    public Guid CircleId { get; set; }
    public int CycleNumber { get; set; }
    public decimal ExpectedAmount { get; set; }
    public decimal PaidAmount { get; set; } = 0;
    public decimal CreditApplied { get; set; } = 0;
    public decimal Balance => ExpectedAmount - CreditApplied - PaidAmount;
    public ContributionStatus Status { get; set; } = ContributionStatus.Pending;
    public string? NombaTransactionRef { get; set; }
    public DateTime DueDate { get; set; }
    public DateTime? PaidAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Member Member { get; set; } = null!;
    public Circle Circle { get; set; } = null!;
}

public class Payout
{
    public Guid Id { get; set; }
    public Guid CircleId { get; set; }
    public Guid MemberId { get; set; }
    public int CycleNumber { get; set; }
    public decimal ExpectedAmount { get; set; }
    public decimal DisbursedAmount { get; set; }
    public PayoutStatus Status { get; set; } = PayoutStatus.Pending;
    public string? NombaTransferRef { get; set; }
    public string? FailureReason { get; set; }
    public int RetryCount { get; set; } = 0;
    public DateTime ScheduledAt { get; set; }
    public DateTime? DisbursedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Circle Circle { get; set; } = null!;
    public Member Member { get; set; } = null!;
}

public class Notification
{
    public Guid Id { get; set; }
    public Guid MemberId { get; set; }
    public NotificationType Type { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool IsRead { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Member Member { get; set; } = null!;
}

// ── Shared Response Wrappers ──────────────────────────────────────────────────

public record ApiResponse<T>(bool Success, T? Data, string? Message = null, IEnumerable<string>? Errors = null)
{
    public static ApiResponse<T> Ok(T data, string? message = null) => new(true, data, message);
    public static ApiResponse<T> Fail(string message, IEnumerable<string>? errors = null) => new(false, default, message, errors);
}

public record PagedResponse<T>(IEnumerable<T> Items, int Total, int Page, int PageSize);

// ── Collection accounts ───────────────────────────────────────────────────────
// A circle's pooled account: every member contribution is swept into it, and
// every payout is debited from it. Provisioned automatically at circle creation.
//
// IMPORTANT — what "balance" means here. Funds paid into any provider virtual
// account are immediately routed into the merchant's parent wallet by the
// provider itself (see TriggerPayoutHandler for why payouts must therefore go to
// a real external bank account, never back into a VA). So this balance is a
// LEDGER position over money that already sits pooled upstream — it records what
// this circle is owed out of that pool. It is not an independently held balance,
// and a sweep is a bookkeeping move, not a wire transfer.

public enum CollectionEntryDirection { Credit, Debit }

public class CollectionAccount
{
    public Guid Id { get; set; }
    public Guid CircleId { get; set; }

    // Which payment provider issued the account — "Nomba", "Paystack", "Stub".
    // Recorded per account so a provider switch leaves the old account readable.
    public string Provider { get; set; } = string.Empty;

    // The provider's own handle for the account (accountRef), plus the NUBAN
    // details we can actually show people.
    public string? ExternalAccountId { get; set; }
    public string AccountNumber { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string BankName { get; set; } = string.Empty;
    public string BankCode { get; set; } = string.Empty;

    // Exactly one active account per circle, enforced by a filtered unique index
    // (see CollectionAccountConfiguration). Superseded accounts stay as history:
    // closed cycles must remain explainable, and a provider migration reissues
    // the account rather than rewriting the old one.
    public bool IsActive { get; set; } = true;

    // TRUE when the provider could not be reached at circle creation and this
    // row is a stand-in, not a real account. Circle creation deliberately does
    // not fail in that case — a provider outage must not block a coordinator
    // from setting up their circle — so the circle gets a placeholder that is
    // obviously not fundable, and an admin provisions the real account later
    // via the backfill endpoint, which upgrades THIS row in place so any ledger
    // entries already raised against it stay attached.
    public bool IsPlaceholder { get; set; }

    // Why provisioning failed, kept so the admin retrying it can see the cause
    // rather than guessing. Cleared when the placeholder is upgraded.
    public string? ProvisioningError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeactivatedAt { get; set; }

    public Circle Circle { get; set; } = null!;
    public ICollection<CollectionLedgerEntry> Entries { get; set; } = new List<CollectionLedgerEntry>();
}

public class CollectionLedgerEntry
{
    public Guid Id { get; set; }
    public Guid CollectionAccountId { get; set; }
    public Guid CircleId { get; set; }

    // Whose money this is. Set on both directions: a credit is a member's
    // contribution coming in, a debit is that circle paying a member out.
    public Guid? MemberId { get; set; }

    public int CycleNumber { get; set; }
    public CollectionEntryDirection Direction { get; set; }
    public decimal Amount { get; set; }

    // What the entry was raised against, so a balance can always be traced back
    // to the contribution or payout that caused it.
    public Guid? ContributionId { get; set; }
    public Guid? PayoutId { get; set; }

    // Idempotency key, uniquely indexed. A contribution sweep uses
    // "SWEEP-{contributionId}", so re-running a sweep cannot double-credit.
    public string Reference { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public CollectionAccount CollectionAccount { get; set; } = null!;
}
