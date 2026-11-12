using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CompanyManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Media_VoiceNotes_Reactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CommentId",
                table: "TaskAttachments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DurationSeconds",
                table: "TaskAttachments",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TaskCommentReactions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CommentId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Emoji = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskCommentReactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskCommentReactions_TaskComments_CommentId",
                        column: x => x.CommentId,
                        principalTable: "TaskComments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TaskCommentReactions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskAttachments_CommentId",
                table: "TaskAttachments",
                column: "CommentId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskCommentReactions_CommentId_UserId_Emoji",
                table: "TaskCommentReactions",
                columns: new[] { "CommentId", "UserId", "Emoji" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskCommentReactions_UserId",
                table: "TaskCommentReactions",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_TaskAttachments_TaskComments_CommentId",
                table: "TaskAttachments",
                column: "CommentId",
                principalTable: "TaskComments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TaskAttachments_TaskComments_CommentId",
                table: "TaskAttachments");

            migrationBuilder.DropTable(
                name: "TaskCommentReactions");

            migrationBuilder.DropIndex(
                name: "IX_TaskAttachments_CommentId",
                table: "TaskAttachments");

            migrationBuilder.DropColumn(
                name: "CommentId",
                table: "TaskAttachments");

            migrationBuilder.DropColumn(
                name: "DurationSeconds",
                table: "TaskAttachments");
        }
    }
}
