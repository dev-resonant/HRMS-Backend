using HRMS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System;
using System.Collections.Generic;
using System.Text;

namespace HRMS.Infrastructure.Persistence.Configurations
{
   public class UserConfiguration : IEntityTypeConfiguration<User>
    {
        public void Configure(EntityTypeBuilder<User> builder)
        {
            // Emails are stored trimmed and lowercase (EmailNormalizer). With the unique (CompanyId, Email) index below this
            // makes uniqueness case-insensitive within a company, and it holds even for raw SQL or future code paths.
            // The SQL trims exactly the characters string.Trim() trims (see UserEmailPolicy).
            builder.ToTable("Users", t => t.HasCheckConstraint(UserEmailPolicy.ConstraintName, UserEmailPolicy.CheckConstraintSql));

            builder.HasKey(u => u.Id);

            builder.Property(u => u.Name).IsRequired().HasMaxLength(100);

            builder.Property(u => u.Email).IsRequired().HasMaxLength(255);

            builder.Property(u => u.PasswordHash).IsRequired();

            builder.Property(u => u.IsActive).IsRequired();

            builder.Property(u => u.IsFirstLogin).IsRequired().HasDefaultValue(false);

            builder.HasIndex(u => new { u.CompanyId, u.Email }).IsUnique();

            builder.HasIndex(u => new { u.CompanyId, u.RoleId });

            builder.HasOne(u => u.Company).WithMany(c => c.Users).HasForeignKey(u => u.CompanyId).OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(u => u.Role).WithMany(r => r.Users).HasForeignKey(u => u.RoleId).OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(u => u.Employee).WithOne(e => e.User).HasForeignKey<Employee>(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
        }
    }
}
