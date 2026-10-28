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
            
            // Platform company: defaults to FALSE for every company, and a unique index over the TRUE rows only means at most one can exist.
            builder.Property(c => c.IsPlatform).IsRequired().HasDefaultValue(false);
            builder.HasIndex(c => c.IsPlatform).IsUnique().HasFilter("\"IsPlatform\"").HasDatabaseName("IX_Companies_IsPlatform_Single");

            //Primary Key
            builder.HasKey(c => c.Id);

            //Properties
            builder.Property(c => c.Name).IsRequired().HasMaxLength(200);

            builder.Property(c => c.Domain).IsRequired().HasMaxLength(100);

            builder.Property(c => c.LogoUrl);

            builder.Property(c => c.Timezone).IsRequired().HasMaxLength(50);

            builder.Property(c => c.IsActive).IsRequired();

            //Unique Index
            builder.HasIndex(c => c.Domain).IsUnique();
        }
    }
}
