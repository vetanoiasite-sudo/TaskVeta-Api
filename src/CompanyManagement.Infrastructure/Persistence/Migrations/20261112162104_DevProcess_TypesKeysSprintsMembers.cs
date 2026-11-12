using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CompanyManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DevProcess_TypesKeysSprintsMembers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BlockedAt",
                table: "Tasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BlockedReason",
                table: "Tasks",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DesignUrl",
                table: "Tasks",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EstimateDays",
                table: "Tasks",
                type: "numeric(4,1)",
                precision: 4,
                scale: 1,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsBlocked",
                table: "Tasks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string[]>(
                name: "Labels",
                table: "Tasks",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<int>(
                name: "Number",
                table: "Tasks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ParentTaskId",
                table: "Tasks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PullRequestUrl",
                table: "Tasks",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScreenRef",
                table: "Tasks",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SprintId",
                table: "Tasks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StoryRef",
                table: "Tasks",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Type",
                table: "Tasks",
                type: "integer",
                nullable: false,
                defaultValue: 1); // existing tasks become Feature

            // Existing tasks get their key numbers (GA-1, GA-2, …) in creation order, per project.
            migrationBuilder.Sql(@"
                UPDATE ""Tasks"" t SET ""Number"" = n.rn
                FROM (SELECT ""Id"", ROW_NUMBER() OVER (PARTITION BY ""ProjectId"" ORDER BY ""Id"") AS rn
                      FROM ""Tasks"" WHERE ""ProjectId"" IS NOT NULL) n
                WHERE t.""Id"" = n.""Id"";");

            migrationBuilder.AddColumn<string>(
                name: "ClientName",
                table: "Projects",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByUserId",
                table: "Projects",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DesignUrl",
                table: "Projects",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DocsUrl",
                table: "Projects",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RepositoryUrl",
                table: "Projects",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProjectMembers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    EmployeeId = table.Column<int>(type: "integer", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectMembers_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectMembers_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Sprints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Goal = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sprints", x => x.Id);
                    table.CheckConstraint("CK_Sprints_Dates", "\"EndDate\" >= \"StartDate\"");
                });

            migrationBuilder.CreateTable(
                name: "TaskDependencies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TaskId = table.Column<int>(type: "integer", nullable: false),
                    BlockedByTaskId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskDependencies", x => x.Id);
                    table.CheckConstraint("CK_TaskDependencies_NotSelf", "\"TaskId\" <> \"BlockedByTaskId\"");
                    table.ForeignKey(
                        name: "FK_TaskDependencies_Tasks_BlockedByTaskId",
                        column: x => x.BlockedByTaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TaskDependencies_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_ParentTaskId",
                table: "Tasks",
                column: "ParentTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_ProjectId_Number",
                table: "Tasks",
                columns: new[] { "ProjectId", "Number" },
                unique: true,
                filter: "\"Number\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_SprintId",
                table: "Tasks",
                column: "SprintId");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_Type",
                table: "Tasks",
                column: "Type");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tasks_Estimate",
                table: "Tasks",
                sql: "\"EstimateDays\" IS NULL OR (\"EstimateDays\" > 0 AND \"EstimateDays\" <= 30)");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_CreatedByUserId",
                table: "Projects",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectMembers_EmployeeId",
                table: "ProjectMembers",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectMembers_ProjectId_EmployeeId_Role",
                table: "ProjectMembers",
                columns: new[] { "ProjectId", "EmployeeId", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sprints_SingleActive",
                table: "Sprints",
                column: "Status",
                unique: true,
                filter: "\"Status\" = 2");

            migrationBuilder.CreateIndex(
                name: "IX_Sprints_StartDate",
                table: "Sprints",
                column: "StartDate");

            migrationBuilder.CreateIndex(
                name: "IX_TaskDependencies_BlockedByTaskId",
                table: "TaskDependencies",
                column: "BlockedByTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskDependencies_TaskId_BlockedByTaskId",
                table: "TaskDependencies",
                columns: new[] { "TaskId", "BlockedByTaskId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Projects_Users_CreatedByUserId",
                table: "Projects",
                column: "CreatedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Tasks_Sprints_SprintId",
                table: "Tasks",
                column: "SprintId",
                principalTable: "Sprints",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Tasks_Tasks_ParentTaskId",
                table: "Tasks",
                column: "ParentTaskId",
                principalTable: "Tasks",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Projects_Users_CreatedByUserId",
                table: "Projects");

            migrationBuilder.DropForeignKey(
                name: "FK_Tasks_Sprints_SprintId",
                table: "Tasks");

            migrationBuilder.DropForeignKey(
                name: "FK_Tasks_Tasks_ParentTaskId",
                table: "Tasks");

            migrationBuilder.DropTable(
                name: "ProjectMembers");

            migrationBuilder.DropTable(
                name: "Sprints");

            migrationBuilder.DropTable(
                name: "TaskDependencies");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_ParentTaskId",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_ProjectId_Number",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_SprintId",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_Type",
                table: "Tasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Tasks_Estimate",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_Projects_CreatedByUserId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "BlockedAt",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "BlockedReason",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "DesignUrl",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "EstimateDays",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "IsBlocked",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "Labels",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "Number",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ParentTaskId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "PullRequestUrl",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ScreenRef",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "SprintId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "StoryRef",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ClientName",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "DesignUrl",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "DocsUrl",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "RepositoryUrl",
                table: "Projects");
        }
    }
}
