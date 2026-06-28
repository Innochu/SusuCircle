namespace SusuCircle.Api.Common.Models;

// ── Enums ─────────────────────────────────────────────────────────────────────

public enum PlanType { BAM, ADASHI }

public enum CircleStatus { Setup, Active, Paused, Completed }

public enum ContributionFrequency { Weekly, Biweekly, Monthly }

public enum PayoutOrderType { Sequential, Random, Bidding }

public enum MemberStatus { Active, Suspended, Completed }

public enum ContributionStatus { Pending, Partial, Paid, Overpaid, Defaulted }

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
}

public class Member
{
    public Guid Id { get; set; }
    public Guid CircleId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string? Email { get; set; }
    public int PayoutPosition { get; set; }
    public string? VirtualAccountId { get; set; }
    public string? VirtualAccountNumber { get; set; }
    public string? BankName { get; set; }
    public MemberStatus Status { get; set; } = MemberStatus.Active;
    public int CreditScore { get; set; } = 50;
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
