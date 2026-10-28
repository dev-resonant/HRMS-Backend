using HRMS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HRMS.Infrastructure.Persistence.Configurations
{
    public class AccountSetupTokenConfiguration : IEntityTypeConfiguration<AccountSetupToken>
    {
        public void Configure(EntityTypeBuilder<AccountSetupToken> builder)
        {
            builder.Property(t => t.TokenHash).IsRequired().HasMaxLength(64);

            // Lookup key for a presented token, and a guarantee that two tokens can never share a hash.
            builder.HasIndex(t => t.TokenHash).IsUnique();

            // "Which invitations does this user have" (re-issue revokes the open ones).
            builder.HasIndex(t => new { t.UserId, t.CreatedAt });

            builder.HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(t => t.Company).WithMany().HasForeignKey(t => t.CompanyId).OnDelete(DeleteBehavior.Restrict);
        }
    }
}
