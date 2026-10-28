using HRMS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HRMS.Infrastructure.Persistence.Configurations
{
    public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
    {
        public void Configure(EntityTypeBuilder<AuditLog> builder)
        {
            builder.Property(a => a.Module).IsRequired().HasMaxLength(50);

            builder.Property(a => a.Action).IsRequired().HasMaxLength(100);

            builder.Property(a => a.IpAddress).HasMaxLength(45);

            builder.Property(a => a.OldValue).HasColumnType("jsonb");

            builder.Property(a => a.NewValue).HasColumnType("jsonb");

            // Audit history is append-only: deleting a user or company must never cascade to audit rows.
            builder.HasOne(a => a.User).WithMany(u => u.AuditLogs).HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(a => a.Company).WithMany().HasForeignKey(a => a.CompanyId).OnDelete(DeleteBehavior.Restrict);

            builder.HasIndex(a => new { a.CompanyId, a.CreatedAt });
        }
    }
}
