using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventRecognizer.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPostRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PostAnalysisCaches");

            migrationBuilder.CreateTable(
                name: "Posts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Url = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Account = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PostId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Caption = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    PostDatetime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ImageUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsEvent = table.Column<bool>(type: "bit", nullable: false),
                    AnalysisJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AnalyzedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Posts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Posts_Url",
                table: "Posts",
                column: "Url",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Posts");

            migrationBuilder.CreateTable(
                name: "PostAnalysisCaches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AnalysisJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PostHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PostId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostAnalysisCaches", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PostAnalysisCaches_PostHash",
                table: "PostAnalysisCaches",
                column: "PostHash",
                unique: true);
        }
    }
}
