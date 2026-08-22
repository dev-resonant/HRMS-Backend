using HRMS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System;
using System.Collections.Generic;
using System.Text;

namespace HRMS.Infrastructure.Persistence.Configurations
{
    public class CompanyConfiguration : IEntityTypeConfiguration<Company>
    {
        public void Configure(EntityTypeBuilder<Company> builder)
        {
            //Table Name
            builder.ToTable("Companies");

            //Primary Key
            builder.HasKey(c => c.Id);

            //Properties
            builder.Property(c => c.Name).IsRequired().HasMaxLength(200);

            builder.Property(c => c.Domain).IsRequired().HasMaxLength(100);

            builder.Property(c => c.LogoUrl).HasMaxLength(50);

            builder.Property(c => c.Timezone).IsRequired().HasMaxLength(50);

            builder.Property(c => c.IsActive).IsRequired();

            //Unique Index
            builder.HasIndex(c => c.Domain).IsUnique();
        }
    }
}
