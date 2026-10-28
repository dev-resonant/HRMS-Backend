using HRMS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HRMS.Infrastructure.Persistence.Configurations
{
    public class SessionConfiguration : IEntityTypeConfiguration<Session>
    {
        public void Configure(EntityTypeBuilder<Session> builder)
        {
            builder.Property(s => s.IpAddress).HasMaxLength(45);

            // Not unique: the JWT has no jti, so two logins by the same user in the same second yield the same token hash.
            builder.HasIndex(s => s.TokenHash);
        }
    }
}
