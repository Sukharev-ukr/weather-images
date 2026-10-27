using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WeatherImages.Services;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

builder.Services.Configure<WeatherImagesOptions>(
    builder.Configuration.GetSection(WeatherImagesOptions.SectionName));

// Azure Storage: blobs (images), queues (jobs) and tables (status) - one storage account.
builder.Services.AddSingleton(_ => StorageClientFactory.CreateBlobServiceClient(builder.Configuration));
builder.Services.AddSingleton(_ => StorageClientFactory.CreateQueueServiceClient(builder.Configuration));
builder.Services.AddSingleton(_ => StorageClientFactory.CreateTableServiceClient(builder.Configuration));

builder.Services.AddSingleton<JobRepository>();
builder.Services.AddSingleton<JobQueues>();
builder.Services.AddSingleton<ImageStore>();

// Outbound HTTP: Buienradar (weather) and the public image API.
builder.Services.AddHttpClient<BuienradarClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("WeatherImages/1.0 (InHolland CCD assignment)");
});
builder.Services.AddHttpClient<ImageSourceClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("WeatherImages/1.0 (InHolland CCD assignment)");
});

builder.Build().Run();
