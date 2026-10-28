using HRMS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HRMS.Infrastructure.Persistence.Configurations
{
    public class SecurityEventConfiguration : IEntityTypeConfiguration<SecurityEvent>
    {
        public void Configure(EntityTypeBuilder<SecurityEvent> builder)
        {
            builder.ToTable("SecurityEvents");

            builder.Property(e => e.EventType).IsRequired().HasMaxLength(60);
            builder.Property(e => e.Reason).IsRequired().HasMaxLength(60);
            builder.Property(e => e.IpAddress).HasMaxLength(45);              // same width as audit_log.ip_address

            builder.HasIndex(e => new { e.CompanyId, e.CreatedAt });
            builder.HasIndex(e => new { e.UserId, e.CreatedAt });

            // The referential integrity that audit_log has is kept for the rows that name an account; the user is simply optional.
            builder.HasOne(e => e.Company).WithMany().HasForeignKey(e => e.CompanyId).OnDelete(DeleteBehavior.Restrict);
            builder.HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);
        }
    }
}
