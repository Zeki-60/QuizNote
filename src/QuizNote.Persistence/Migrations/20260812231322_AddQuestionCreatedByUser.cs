using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuizNote.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQuestionCreatedByUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByUserId",
                table: "questions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_questions_CreatedByUserId",
                table: "questions",
                column: "CreatedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_questions_users_CreatedByUserId",
                table: "questions",
                column: "CreatedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_questions_users_CreatedByUserId",
                table: "questions");

            migrationBuilder.DropIndex(
                name: "IX_questions_CreatedByUserId",
                table: "questions");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "questions");
        }
    }
}
