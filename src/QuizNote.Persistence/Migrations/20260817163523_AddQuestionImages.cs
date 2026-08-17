using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuizNote.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQuestionImages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ImageId",
                table: "questions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "question_images",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    StoredFileName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_question_images", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_questions_ImageId",
                table: "questions",
                column: "ImageId");

            migrationBuilder.AddForeignKey(
                name: "FK_questions_question_images_ImageId",
                table: "questions",
                column: "ImageId",
                principalTable: "question_images",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_questions_question_images_ImageId",
                table: "questions");

            migrationBuilder.DropTable(
                name: "question_images");

            migrationBuilder.DropIndex(
                name: "IX_questions_ImageId",
                table: "questions");

            migrationBuilder.DropColumn(
                name: "ImageId",
                table: "questions");
        }
    }
}
