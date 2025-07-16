namespace AsyncJobProcessor.Models;

public class JobRequest
{
    public string Data { get; set; } = string.Empty;
    public string RequestType { get; set; } = string.Empty;
}