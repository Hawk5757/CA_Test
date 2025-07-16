namespace AsyncJobProcessor.Models;

public class JobData
{
    public string JobId { get; set; } = string.Empty;
    public JobStatus Status { get; set; }
    public string RequestData { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ResultPayload { get; set; }
    public string? ErrorMessage { get; set; }
}