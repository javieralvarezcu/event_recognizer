using EventRecognizer.Api.Data;
using EventRecognizer.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EventRecognizer.Api.Services;

public class DeepSeekAuditService : IDeepSeekAuditService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ILogger<DeepSeekAuditService> _logger;

    public DeepSeekAuditService(IDbContextFactory<AppDbContext> dbContextFactory, ILogger<DeepSeekAuditService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    /// <summary>
    /// Saves one audit row on its own DbContext instance, so the save never
    /// touches the pending changes of the scoped context used by the request
    /// flow. Failures are swallowed: auditing must not break the LLM call.
    /// </summary>
    public async Task RecordAsync(DeepSeekCallLog log, CancellationToken ct = default)
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            db.DeepSeekCallLogs.Add(log);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist DeepSeek audit log for operation {Operation}", log.Operation);
        }
    }
}
