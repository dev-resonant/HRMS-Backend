using System;
using System.Collections.Generic;
using System.Text;
using HRMS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HRMS.Infrastructure.Persistence.Configurations
{
    public class DepartmentConfiguration : IEntityTypeConfiguration<Department>
    {
        public void Configure(EntityTypeBuilder<Department> builder)
        {
            //Company -> Departments
            builder.HasOne(d => d.Company).WithMany(c => c.Departments).HasForeignKey(d => d.CompanyId).OnDelete(DeleteBehavior.Cascade);

            //Department -> Employees
            builder.HasMany(d => d.Employees).WithOne(e => e.Department).HasForeignKey(e => e.DepartmentId).OnDelete(DeleteBehavior.Restrict);

            //Depaartment -> HeadEmployee
            builder.HasOne(d => d.HeadEmployee).WithMany().HasForeignKey(d => d.HeadEmpId).OnDelete(DeleteBehavior.SetNull);

            //Department -> Parent Department

            builder.HasOne(d => d.ParentDepartment).WithMany(d => d.ChildDepartments).HasForeignKey(d => d.ParentDeptId).OnDelete(DeleteBehavior.Restrict);
        }
    }
}
