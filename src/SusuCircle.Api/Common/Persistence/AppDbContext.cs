using Microsoft.EntityFrameworkCore;
using SusuCircle.Api.Common.Models;
using SusuCircle.Api.Common.Persistence.Configurations;

namespace SusuCircle.Api.Common.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Admin> Admins => Set<Admin>();
    public DbSet<Circle> Circles => Set<Circle>();
    public DbSet<Member> Members => Set<Member>();
    public DbSet<Contribution> Contributions => Set<Contribution>();
    public DbSet<Payout> Payouts => Set<Payout>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<UnmatchedTransaction> UnmatchedTransactions => Set<UnmatchedTransaction>();
    public DbSet<AdminNotification> AdminNotifications => Set<AdminNotification>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.ApplyConfiguration(new AdminConfiguration());
        builder.ApplyConfiguration(new CircleConfiguration());
        builder.ApplyConfiguration(new MemberConfiguration());
        builder.ApplyConfiguration(new ContributionConfiguration());
        builder.ApplyConfiguration(new PayoutConfiguration());
        builder.ApplyConfiguration(new NotificationConfiguration());
        base.OnModelCreating(builder);
    }
}