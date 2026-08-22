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
            builder.ToTable("Users");

            builder.HasKey(u => u.Id);

            builder.Property(u => u.Name).IsRequired().HasMaxLength(100);

            builder.Property(u => u.Email).IsRequired().HasMaxLength(255);

            builder.Property(u => u.PasswordHash).IsRequired();

            builder.Property(u => u.IsActive).IsRequired();

            builder.HasIndex(u => new { u.CompanyId, u.Email }).IsUnique();

            builder.HasOne(u => u.Company).WithMany(c => c.Users).HasForeignKey(u => u.CompanyId).OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(u => u.Role).WithMany(r => r.Users).HasForeignKey(u => u.RoleId).OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(u => u.Employee).WithOne(e => e.User).HasForeignKey<Employee>(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
        }
    }
}
