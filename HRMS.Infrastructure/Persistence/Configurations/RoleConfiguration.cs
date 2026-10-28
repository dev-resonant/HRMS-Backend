using HRMS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HRMS.Infrastructure.Persistence.Configurations
{
    public class RoleConfiguration : IEntityTypeConfiguration<Role>
    {
        public void Configure(EntityTypeBuilder<Role> builder)
        {
            builder.Property(r => r.Name).IsRequired().HasMaxLength(50);

            builder.Property(r => r.DisplayName).IsRequired().HasMaxLength(100);

            builder.Property(r => r.Permissions).IsRequired().HasColumnType("jsonb");

            // Optimistic concurrency without a schema column: Npgsql maps a uint row version to the system column xmin (same as Employee).
            builder.Property(r => r.Version).IsRowVersion();

            // System roles (CompanyId NULL) are identified by their stable key; there is exactly one of each.
            builder.HasIndex(r => r.Name).IsUnique().HasFilter("\"CompanyId\" IS NULL").HasDatabaseName("IX_Roles_Name_System");
        }
    }
}
