using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InterviewCopilot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Wave3Schema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LlmProvider",
                table: "UserPreferences",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpeechToTextProvider",
                table: "UserPreferences",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PairedBrowsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastUsedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PairedBrowsers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PairedBrowsers_UserAccounts_UserId",
                        column: x => x.UserId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PairedBrowsers_TokenHash",
                table: "PairedBrowsers",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PairedBrowsers_UserId",
                table: "PairedBrowsers",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PairedBrowsers");

            migrationBuilder.DropColumn(
                name: "LlmProvider",
                table: "UserPreferences");

            migrationBuilder.DropColumn(
                name: "SpeechToTextProvider",
                table: "UserPreferences");
        }
    }
}
