using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompanyManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Settings_SprintLength_CapacityTarget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CapacityBugsPercent",
                table: "SystemSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CapacityImprovePercent",
                table: "SystemSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CapacityNewPercent",
                table: "SystemSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SprintLengthDays",
                table: "SystemSettings",
                type: "integer",
                nullable: false,
                defaultValue: 7);

            migrationBuilder.AddCheckConstraint(
                name: "CK_SystemSettings_CapacityTarget",
                table: "SystemSettings",
                sql: "(\"CapacityNewPercent\" IS NULL AND \"CapacityImprovePercent\" IS NULL AND \"CapacityBugsPercent\" IS NULL) OR (\"CapacityNewPercent\" + \"CapacityImprovePercent\" + \"CapacityBugsPercent\" = 100)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SystemSettings_CapacityTarget",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "CapacityBugsPercent",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "CapacityImprovePercent",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "CapacityNewPercent",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "SprintLengthDays",
                table: "SystemSettings");
        }
    }
}
