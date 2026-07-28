using EventRecognizer.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EventRecognizer.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<EventRecord> EventRecords => Set<EventRecord>();

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
    }
}
