using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventRecognizer.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMuxoEventsAndCrossMatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MuxoEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ExternalId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Date = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Venue = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Link = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Categories = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Price = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MuxoEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CrossMatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventUniqueId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    MuxoEventId = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrossMatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CrossMatches_MuxoEvents_MuxoEventId",
                        column: x => x.MuxoEventId,
                        principalTable: "MuxoEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CrossMatches_EventUniqueId",
                table: "CrossMatches",
                column: "EventUniqueId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CrossMatches_MuxoEventId",
                table: "CrossMatches",
                column: "MuxoEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MuxoEvents_ExternalId",
                table: "MuxoEvents",
                column: "ExternalId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CrossMatches");

            migrationBuilder.DropTable(
                name: "MuxoEvents");
        }
    }
}
