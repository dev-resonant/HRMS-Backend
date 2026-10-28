using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HRMS.Infrastructure.Migrations
{
    /// <summary>
    /// Snapshot-only migration (same convention as <c>EmployeeConcurrencyToken</c>). <c>Role.Version</c> is mapped to PostgreSQL's built-in
    /// <c>xmin</c> system column as an optimistic-concurrency token, so the database needs no change. EF scaffolds an <c>AddColumn "xmin"</c>
    /// for such a property; that statement would fail (a column named xmin already exists as a system column) and must never create a
    /// physical column, so it was removed on purpose. The migration exists so the model snapshot matches the model: EF Core 9+ refuses to
    /// run <c>Migrate()</c> while the model has pending changes, and both seeders call it at application start.
    /// Up and Down are therefore intentionally empty and execute no schema or data change.
    /// </summary>
    public partial class RoleConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
