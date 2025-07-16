using AsyncJobProcessor.Models;

namespace AsyncJobProcessor.Interfaces
{
    public interface IJobService
    {
        Task<JobResult> RegisterAndProcessJobAsync(JobRequest request, CancellationToken cancellationToken);
        Task<bool> ProcessJobCallbackAsync(string jobId, JobResult result);
        Task SaveJobAsync(JobData job);
    }
}