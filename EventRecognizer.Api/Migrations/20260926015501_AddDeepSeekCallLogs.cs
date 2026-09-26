using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventRecognizer.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDeepSeekCallLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeepSeekCallLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Operation = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ContextSummary = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SystemPrompt = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UserPrompt = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ResponseContent = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Model = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    MaxTokens = table.Column<int>(type: "int", nullable: false),
                    Temperature = table.Column<double>(type: "float", nullable: false),
                    PromptTokens = table.Column<int>(type: "int", nullable: false),
                    CompletionTokens = table.Column<int>(type: "int", nullable: false),
                    TotalTokens = table.Column<int>(type: "int", nullable: false),
                    TokensEstimated = table.Column<bool>(type: "bit", nullable: false),
                    Attempt = table.Column<int>(type: "int", nullable: false),
                    Succeeded = table.Column<bool>(type: "bit", nullable: false),
                    HttpStatusCode = table.Column<int>(type: "int", nullable: true),
                    FinishReason = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeepSeekCallLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeepSeekCallLogs_Operation",
                table: "DeepSeekCallLogs",
                column: "Operation");

            migrationBuilder.CreateIndex(
                name: "IX_DeepSeekCallLogs_StartedAtUtc",
                table: "DeepSeekCallLogs",
                column: "StartedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeepSeekCallLogs");
        }
    }
}
