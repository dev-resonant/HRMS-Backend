using HRMS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Text;

namespace HRMS.Infrastructure.Persistence
{
    public class HrmsDbContext : DbContext
    {
        public HrmsDbContext(DbContextOptions<HrmsDbContext> options) : base(options) { }

        public DbSet<Company> Companies => Set<Company>();

        public DbSet<Role> Roles => Set<Role>();

        public DbSet<User> Users => Set<User>();

        public DbSet<Employee> Employees => Set<Employee>();

        public DbSet<Department> Departments => Set<Department>();

        public DbSet<Session> Sessions => Set<Session>();

        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

        public DbSet<AccountSetupToken> AccountSetupTokens => Set<AccountSetupToken>();

        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

        public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();

        // The audit log is append-only (specification Table 7 / section 7): SaveChanges refuses to update or delete an entry.
        // This guards ordinary entity-tracked code. It cannot see ExecuteUpdate/ExecuteDelete or raw SQL; a database-level
        // control (trigger or privileges) would be needed for that and is documented as a follow-up.
        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            EnsureAuditLogIsAppendOnly();
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            EnsureAuditLogIsAppendOnly();
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        private void EnsureAuditLogIsAppendOnly()
        {
            if (ChangeTracker.Entries<AuditLog>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            {
                throw new InvalidOperationException("Audit log entries are append-only and cannot be updated or deleted.");
            }

            if (ChangeTracker.Entries<SecurityEvent>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            {
                throw new InvalidOperationException("Security events are append-only and cannot be updated or deleted.");
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.ApplyConfigurationsFromAssembly(typeof(HrmsDbContext).Assembly);
        }
    }
}
