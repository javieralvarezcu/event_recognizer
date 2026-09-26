using EventRecognizer.Api.Models;

namespace EventRecognizer.Api.Services;

/// <summary>
/// Persists audit rows of the exchanges with the DeepSeek API. A failed audit
/// write must never break the LLM flow it is auditing.
/// </summary>
public interface IDeepSeekAuditService
{
    Task RecordAsync(DeepSeekCallLog log, CancellationToken ct = default);
}
