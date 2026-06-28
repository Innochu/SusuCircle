using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SusuCircle.Api.Common.Models;

namespace SusuCircle.Api.Common.Persistence.Configurations;

public class AdminConfiguration : IEntityTypeConfiguration<Admin>
{
    public void Configure(EntityTypeBuilder<Admin> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired().HasMaxLength(120);
        b.Property(x => x.Email).IsRequired().HasMaxLength(200);
        b.HasIndex(x => x.Email).IsUnique();
        b.Property(x => x.Phone).IsRequired().HasMaxLength(20);
        b.Property(x => x.PasswordHash).IsRequired();
    }
}

public class CircleConfiguration : IEntityTypeConfiguration<Circle>
{
    public void Configure(EntityTypeBuilder<Circle> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired().HasMaxLength(150);
        b.Property(x => x.ContributionAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.Plan).HasConversion<string>();
        b.Property(x => x.Status).HasConversion<string>();
        b.Property(x => x.Frequency).HasConversion<string>();
        b.Property(x => x.PayoutOrder).HasConversion<string>();
        b.HasOne(x => x.Admin).WithMany(a => a.Circles).HasForeignKey(x => x.AdminId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class MemberConfiguration : IEntityTypeConfiguration<Member>
{
    public void Configure(EntityTypeBuilder<Member> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired().HasMaxLength(120);
        b.Property(x => x.Phone).IsRequired().HasMaxLength(20);
        b.Property(x => x.Status).HasConversion<string>();
        b.HasIndex(x => x.VirtualAccountNumber).IsUnique();
        b.HasOne(x => x.Circle).WithMany(c => c.Members).HasForeignKey(x => x.CircleId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class ContributionConfiguration : IEntityTypeConfiguration<Contribution>
{
    public void Configure(EntityTypeBuilder<Contribution> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.ExpectedAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.PaidAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.CreditApplied).HasColumnType("decimal(18,2)");
        b.Property(x => x.Status).HasConversion<string>();
        b.Ignore(x => x.Balance);
        b.HasIndex(x => x.NombaTransactionRef).IsUnique();
        b.HasOne(x => x.Member).WithMany(m => m.Contributions).HasForeignKey(x => x.MemberId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Circle).WithMany(c => c.Contributions).HasForeignKey(x => x.CircleId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class PayoutConfiguration : IEntityTypeConfiguration<Payout>
{
    public void Configure(EntityTypeBuilder<Payout> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.ExpectedAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.DisbursedAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.Status).HasConversion<string>();
        b.HasOne(x => x.Circle).WithMany(c => c.Payouts).HasForeignKey(x => x.CircleId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Member).WithMany(m => m.Payouts).HasForeignKey(x => x.MemberId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).IsRequired().HasMaxLength(200);
        b.Property(x => x.Body).IsRequired();
        b.Property(x => x.Type).HasConversion<string>();
        b.HasOne(x => x.Member).WithMany(m => m.Notifications).HasForeignKey(x => x.MemberId).OnDelete(DeleteBehavior.Cascade);
    }
}
