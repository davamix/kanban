using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KanbanApi.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectCalendarMirror : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CalendarProjectId",
                table: "projects",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MirrorStatus",
                table: "projects",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                // Backfill existing rows with a valid enum name (empty string wouldn't round-trip via
                // the string↔enum conversion). Existing projects predate mirroring → Skipped.
                defaultValue: "Skipped");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CalendarProjectId",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "MirrorStatus",
                table: "projects");
        }
    }
}
