using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Configure Application Insights
builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// Configure logging with Application Insights
builder.Logging.AddApplicationInsights();
// Set minimum log level for Application Insights
builder.Logging.SetMinimumLevel(LogLevel.Information);

// Ensure Application Insights is properly configured
var configuration = builder.Configuration;
var appInsightsConnectionString = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];

if (string.IsNullOrEmpty(appInsightsConnectionString))
{
    Console.WriteLine("Warning: APPLICATIONINSIGHTS_CONNECTION_STRING is not configured!");
}

builder.Build().Run();
