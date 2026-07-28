using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EventRecognizer.Api.Data;

/// <summary>
/// Design-time factory for EF Core migrations CLI (dotnet ef migrations add).
/// Not used at runtime.
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseSqlServer("Server=localhost;Database=EventRecognizerDb;TrustServerCertificate=true");
        return new AppDbContext(optionsBuilder.Options);
    }
}
