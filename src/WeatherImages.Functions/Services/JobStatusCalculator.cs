using WeatherImages.Models;

namespace WeatherImages.Services;

public readonly record struct JobProgress(string Status, int Total, int Completed, int Failed)
{
    public int Done => Completed + Failed;
    public bool IsFinished => Status is JobStates.Completed or JobStates.CompletedWithErrors or JobStates.Failed;
    public int Percent => Total == 0 ? 0 : (int)Math.Round(100.0 * Done / Total);
}

/// <summary>Derives the overall job status from the job row and the per-image rows.</summary>
public static class JobStatusCalculator
{
    public static JobProgress Compute(JobEntity job, IReadOnlyCollection<JobImageEntity> images)
    {
        var completed = images.Count(i => i.Status == ImageStates.Completed);
        var failed = images.Count(i => i.Status == ImageStates.Failed);

        // The job itself failed (e.g. Buienradar was down on every attempt) or has not started yet.
        if (job.Status is JobStates.Failed or JobStates.Queued)
        {
            return new JobProgress(job.Status, job.Total, completed, failed);
        }

        // Running, but the fan-out is not finished yet (total unknown).
        if (job.Total == 0)
        {
            return new JobProgress(JobStates.Running, 0, completed, failed);
        }

        if (completed + failed < job.Total)
        {
            return new JobProgress(JobStates.Running, job.Total, completed, failed);
        }

        var status = failed == 0 ? JobStates.Completed
            : completed == 0 ? JobStates.Failed
            : JobStates.CompletedWithErrors;

        return new JobProgress(status, job.Total, completed, failed);
    }
}
