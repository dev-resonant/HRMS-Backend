using HRMS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HRMS.Infrastructure.Persistence.Configurations
{
    public class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
    {
        public const string StatusConstraintName = "CK_OutboxMessages_Status";

        public void Configure(EntityTypeBuilder<OutboxMessage> builder)
        {
            var allowed = string.Join(", ", OutboxStatus.All.Select(s => $"'{s}'"));

            builder.ToTable("OutboxMessages", t =>
            {
                t.HasCheckConstraint(StatusConstraintName, $"\"Status\" IN ({allowed})");
                t.HasCheckConstraint("CK_OutboxMessages_Attempts", "\"Attempts\" >= 0");
            });

            builder.Property(m => m.Kind).IsRequired().HasMaxLength(50);
            builder.Property(m => m.Status).IsRequired().HasMaxLength(20);
            builder.Property(m => m.LastError).HasMaxLength(100);

            // Idempotency key: a token can be the subject of at most one message, so a replayed enqueue cannot create a second delivery.
            builder.HasIndex(m => m.SetupTokenId).IsUnique();

            // The worker's claim query: due Pending rows, expired Processing leases.
            builder.HasIndex(m => new { m.Status, m.NextAttemptAt });

            builder.HasIndex(m => m.CompanyId);
            builder.HasIndex(m => m.UserId);

            builder.HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Restrict);
            builder.HasOne(m => m.Company).WithMany().HasForeignKey(m => m.CompanyId).OnDelete(DeleteBehavior.Restrict);
            builder.HasOne(m => m.SetupToken).WithMany().HasForeignKey(m => m.SetupTokenId).OnDelete(DeleteBehavior.Restrict);
        }
    }
}
