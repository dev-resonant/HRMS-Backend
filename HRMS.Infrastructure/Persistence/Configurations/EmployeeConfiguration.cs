using HRMS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HRMS.Infrastructure.Persistence.Configurations
{
    public class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
    {
        public void Configure(EntityTypeBuilder<Employee> builder)
        {
            // Optimistic concurrency without a schema column: Npgsql maps a uint row version to the system column xmin.
            builder.Property(e => e.Version).IsRowVersion();

            builder.Property(e => e.EmpCode).IsRequired().HasMaxLength(20);

            builder.Property(e => e.Designation).IsRequired().HasMaxLength(100);

            // Optional onboarding-form fields that Table 4 does not list (see docs: decision D7). Additive and nullable.
            builder.Property(e => e.PersonalEmail).HasMaxLength(255);

            builder.Property(e => e.Phone).HasMaxLength(20);

            builder.Property(e => e.JoiningDate).IsRequired();

            // emp_code is unique per company
            builder.HasIndex(e => new { e.CompanyId, e.EmpCode }).IsUnique();

            // list employees of a company by status
            builder.HasIndex(e => new { e.CompanyId, e.Status });
        }
    }
}
