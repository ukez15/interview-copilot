using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InterviewCopilot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SpeechModelPreference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SpeechModel",
                table: "UserPreferences",
                type: "TEXT",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SpeechModel",
                table: "UserPreferences");
        }
    }
}
