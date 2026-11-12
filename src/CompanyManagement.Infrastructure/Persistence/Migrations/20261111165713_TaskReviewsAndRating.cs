using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CompanyManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TaskReviewsAndRating : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Rating",
                table: "Tasks",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TaskReviews",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TaskId = table.Column<int>(type: "integer", nullable: false),
                    ReviewerUserId = table.Column<int>(type: "integer", nullable: false),
                    Approved = table.Column<bool>(type: "boolean", nullable: false),
                    Rating = table.Column<int>(type: "integer", nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskReviews", x => x.Id);
                    table.CheckConstraint("CK_TaskReviews_Rating", "\"Rating\" IS NULL OR \"Rating\" BETWEEN 1 AND 10");
                    table.ForeignKey(
                        name: "FK_TaskReviews_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TaskReviews_Users_ReviewerUserId",
                        column: x => x.ReviewerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tasks_Rating",
                table: "Tasks",
                sql: "\"Rating\" IS NULL OR \"Rating\" BETWEEN 1 AND 10");

            migrationBuilder.CreateIndex(
                name: "IX_TaskReviews_CreatedAt",
                table: "TaskReviews",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TaskReviews_ReviewerUserId",
                table: "TaskReviews",
                column: "ReviewerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskReviews_TaskId_CreatedAt",
                table: "TaskReviews",
                columns: new[] { "TaskId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskReviews");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Tasks_Rating",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "Rating",
                table: "Tasks");
        }
    }
}
