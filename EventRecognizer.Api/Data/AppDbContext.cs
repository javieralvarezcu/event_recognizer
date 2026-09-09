using EventRecognizer.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EventRecognizer.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<EventRecord> EventRecords => Set<EventRecord>();

    public DbSet<MuxoEvent> MuxoEvents => Set<MuxoEvent>();

    public DbSet<CrossMatch> CrossMatches => Set<CrossMatch>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EventRecord>(entity =>
        {
            entity.HasIndex(e => e.EventUniqueId)
                  .IsUnique();

            entity.HasIndex(e => e.PostId)
                  .IsUnique();

            entity.HasIndex(e => e.Account);

            entity.HasIndex(e => e.EventDate);

            entity.HasIndex(e => e.CreatedAt);
        });

        modelBuilder.Entity<MuxoEvent>(entity =>
        {
            entity.HasIndex(e => e.ExternalId)
                  .IsUnique();
        });

        modelBuilder.Entity<CrossMatch>(entity =>
        {
            entity.HasIndex(m => m.EventUniqueId)
                  .IsUnique();

            entity.HasIndex(m => m.MuxoEventId)
                  .IsUnique();

            entity.HasOne(m => m.MuxoEvent)
                  .WithMany()
                  .HasForeignKey(m => m.MuxoEventId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
