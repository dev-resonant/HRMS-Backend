using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HRMS.Infrastructure.Migrations
{
    /// <summary>
    /// Adds <c>Companies.IsPlatform</c> (boolean, NOT NULL, default FALSE) and a unique PARTIAL index over the TRUE rows, so at most one
    /// company can ever be the platform company. Purely additive: every existing company gets FALSE (nothing is designated here, and
    /// application start-up never designates one either - an authorized operator does that explicitly, see docs/PLATFORM_ADMINISTRATION.md),
    /// no existing value is touched, and <c>Down</c> removes exactly what <c>Up</c> added. Adding a column with a constant default is a
    /// metadata-only change in PostgreSQL 11+ (no table rewrite). The lock timeout makes it fail fast instead of queueing behind a long
    /// transaction and blocking every login.
    /// </summary>
    public partial class AddCompanyIsPlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout = '15s';");

            migrationBuilder.AddColumn<bool>(
                name: "IsPlatform",
                table: "Companies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Companies_IsPlatform_Single",
                table: "Companies",
                column: "IsPlatform",
                unique: true,
                filter: "\"IsPlatform\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Companies_IsPlatform_Single",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "IsPlatform",
                table: "Companies");
        }
    }
}
