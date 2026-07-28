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
            // Idempotent: IF OBJECT_ID guard allows safe transition from EnsureCreated() to Migrate().
            // If the table already exists (created by EnsureCreated), this migration is a no-op
            // but still gets recorded in __EFMigrationsHistory so future migrations apply correctly.
            // If the table doesn't exist (fresh install), it creates the full schema.
            migrationBuilder.Sql(@"
                IF OBJECT_ID(N'[EventRecords]', N'U') IS NULL
                BEGIN
                    CREATE TABLE [EventRecords] (
                        [Id] int NOT NULL IDENTITY,
                        [Account] nvarchar(200) NOT NULL,
                        [Caption] nvarchar(500) NOT NULL,
                        [CreatedAt] datetime2 NOT NULL,
                        [EventDate] datetime2 NULL,
                        [EventDateDescription] nvarchar(500) NULL,
                        [EventUniqueId] nvarchar(50) NOT NULL,
                        [ImageUrl] nvarchar(1000) NULL,
                        [PostDatetime] datetime2 NULL,
                        [PostId] nvarchar(100) NOT NULL,
                        [Summary] nvarchar(2000) NOT NULL,
                        [Title] nvarchar(300) NOT NULL,
                        [Url] nvarchar(500) NOT NULL,
                        CONSTRAINT [PK_EventRecords] PRIMARY KEY ([Id])
                    );

                    CREATE UNIQUE INDEX [IX_EventRecords_EventUniqueId] ON [EventRecords] ([EventUniqueId]);
                    CREATE UNIQUE INDEX [IX_EventRecords_PostId] ON [EventRecords] ([PostId]);
                    CREATE INDEX [IX_EventRecords_Account] ON [EventRecords] ([Account]);
                    CREATE INDEX [IX_EventRecords_CreatedAt] ON [EventRecords] ([CreatedAt]);
                    CREATE INDEX [IX_EventRecords_EventDate] ON [EventRecords] ([EventDate]);
                END
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS [EventRecords]");
        }
    }
}
