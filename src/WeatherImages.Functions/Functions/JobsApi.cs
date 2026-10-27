using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WeatherImages.Models;
using WeatherImages.Services;

namespace WeatherImages.Functions;

/// <summary>
/// Public HTTP API.
///   POST /api/jobs                 start a job (needs x-api-key)  -> 202 + jobId
///   GET  /api/jobs/{id}            status / progress
///   GET  /api/jobs/{id}/images     links to the generated images (200 when finished, 202 while running)
/// </summary>
public sealed class JobsApi(
    JobRepository jobs,
    JobQueues queues,
    ImageStore imageStore,
    IOptions<WeatherImagesOptions> options,
    ILogger<JobsApi> logger)
{
    [Function("StartJob")]
    public async Task<IActionResult> StartJob(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "jobs")] HttpRequest req,
        CancellationToken ct)
    {
        var o = options.Value;

        if (!ApiKeyAuthorizer.IsAuthorized(req, o.ApiKey))
        {
            return Problem(StatusCodes.Status401Unauthorized, $"Missing or invalid '{ApiKeyAuthorizer.HeaderName}' header.");
        }

        var (count, error) = await ReadCountAsync(req, o.MaxStations, ct);
        if (error is not null)
        {
            return Problem(StatusCodes.Status400BadRequest, error);
        }

        var jobId = Guid.NewGuid().ToString("N");

        // 1. Save the job (status Queued)  2. put it on the "start" queue  3. answer immediately.
        await jobs.CreateAsync(jobId, count, ct);
        await queues.EnqueueStartAsync(new StartJobMessage(jobId, count), ct);

        logger.LogInformation("Job {JobId} queued for {Count} stations", jobId, count);

        var statusUrl = $"{BaseUrl(req)}/api/jobs/{jobId}";
        var response = new StartJobResponse(jobId, JobStates.Queued, statusUrl, $"{statusUrl}/images");

        req.HttpContext.Response.Headers.Location = statusUrl;
        return new ObjectResult(response) { StatusCode = StatusCodes.Status202Accepted };
    }

    [Function("GetJobStatus")]
    public async Task<IActionResult> GetJobStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "jobs/{jobId}")] HttpRequest req,
        string jobId,
        CancellationToken ct)
    {
        if (!TryNormalizeJobId(jobId, out var id))
        {
            return Problem(StatusCodes.Status404NotFound, "Unknown job.");
        }

        var job = await jobs.GetAsync(id, ct);
        if (job is null)
        {
            return Problem(StatusCodes.Status404NotFound, "Unknown job.");
        }

        var progress = JobStatusCalculator.Compute(job, await jobs.ListImagesAsync(id, ct));

        return new OkObjectResult(new JobStatusResponse(
            id, progress.Status, job.Requested, progress.Total, progress.Completed, progress.Failed, progress.Percent,
            job.CreatedAt, job.StartedAt, string.IsNullOrEmpty(job.Error) ? null : job.Error,
            $"{BaseUrl(req)}/api/jobs/{id}/images"));
    }

    [Function("GetJobImages")]
    public async Task<IActionResult> GetJobImages(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "jobs/{jobId}/images")] HttpRequest req,
        string jobId,
        CancellationToken ct)
    {
        if (!TryNormalizeJobId(jobId, out var id))
        {
            return Problem(StatusCodes.Status404NotFound, "Unknown job.");
        }

        var job = await jobs.GetAsync(id, ct);
        if (job is null)
        {
            return Problem(StatusCodes.Status404NotFound, "Unknown job.");
        }

        var rows = await jobs.ListImagesAsync(id, ct);
        var progress = JobStatusCalculator.Compute(job, rows);

        var finished = rows
            .Where(r => r.Status == ImageStates.Completed && !string.IsNullOrEmpty(r.BlobName))
            .OrderBy(r => r.StationName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var (links, expiresAt) = await imageStore.CreateReadLinksAsync(
            finished.Select(r => r.BlobName!).ToList(),
            TimeSpan.FromMinutes(options.Value.SasExpiryMinutes),
            ct);

        var images = finished
            .Select(r => new ImageLink(r.StationId, r.StationName, r.Region, Path.GetFileName(r.BlobName!), links[r.BlobName!].ToString()))
            .ToList();

        var body = new JobImagesResponse(
            id, progress.Status, progress.IsFinished, progress.Total, progress.Completed, progress.Failed,
            images.Count > 0 ? expiresAt : null, images);

        if (!progress.IsFinished)
        {
            // Not done yet: 202 Accepted with the images that are ready so far; poll again later.
            req.HttpContext.Response.Headers["Retry-After"] = "5";
            return new ObjectResult(body) { StatusCode = StatusCodes.Status202Accepted };
        }

        return new OkObjectResult(body);
    }

    // ---------- helpers ----------

    private static async Task<(int Count, string? Error)> ReadCountAsync(HttpRequest req, int max, CancellationToken ct)
    {
        int? requested = null;

        if (req.Query.TryGetValue("count", out var q) && q.Count > 0)
        {
            if (!int.TryParse(q[0], out var parsed))
            {
                return (0, "'count' must be a number.");
            }

            requested = parsed;
        }
        else if (req.ContentLength is > 0)
        {
            try
            {
                var body = await req.ReadFromJsonAsync<StartJobRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
                requested = body?.Count;
            }
            catch (JsonException)
            {
                return (0, "Body must be JSON like { \"count\": 10 } (or send no body).");
            }
        }

        var count = requested ?? max;
        return count is < 1 || count > max
            ? (0, $"'count' must be between 1 and {max}.")
            : (count, null);
    }

    private static bool TryNormalizeJobId(string value, out string normalized)
    {
        if (Guid.TryParse(value, out var guid))
        {
            normalized = guid.ToString("N");
            return true;
        }

        normalized = "";
        return false;
    }

    private static string BaseUrl(HttpRequest req)
    {
        // Behind the Azure front end the request may look like http; the public endpoint is always https.
        var host = req.Host.Host;
        var local = host is "localhost" or "127.0.0.1";
        return $"{(local ? req.Scheme : "https")}://{req.Host}";
    }

    private static ObjectResult Problem(int status, string detail) =>
        new(new ProblemDetails { Status = status, Detail = detail }) { StatusCode = status };
}
