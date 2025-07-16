namespace AsyncJobProcessor.Models
{
    public enum JobStatus
    {
        Pending,
        Completed,
        Failed,
        Cancelled,
        Timeout
    }
}