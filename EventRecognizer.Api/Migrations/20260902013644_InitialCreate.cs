using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventRecognizer.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventUniqueId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    EventDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EventDateDescription = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Summary = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    IsRecurrent = table.Column<bool>(type: "bit", nullable: false),
                    RecurrenceType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    RecurrenceDaysOfWeek = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    RecurrenceStartDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RecurrenceEndDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Account = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PostId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Caption = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    PostDatetime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Url = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ImageUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventRecords_Account",
                table: "EventRecords",
                column: "Account");

            migrationBuilder.CreateIndex(
                name: "IX_EventRecords_CreatedAt",
                table: "EventRecords",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_EventRecords_EventDate",
                table: "EventRecords",
                column: "EventDate");

            migrationBuilder.CreateIndex(
                name: "IX_EventRecords_EventUniqueId",
                table: "EventRecords",
                column: "EventUniqueId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventRecords_PostId",
                table: "EventRecords",
                column: "PostId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventRecords");
        }
    }
}
