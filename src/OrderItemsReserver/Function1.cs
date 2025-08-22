using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace OrderItemsReserver;

public static class Function
{
    [Function("Function1")]
    public static async Task<List<string>> RunOrchestrator(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        ILogger logger = context.CreateReplaySafeLogger(nameof(Function));

        string json = context.GetInput<string>();

        string result = await context.CallActivityAsync<string>("SaveOrderToBlob", json);

        return new List<string> { result };
    }

    [Function("SaveOrderToBlob")]
    public static async Task<string> SaveOrderToBlob([ActivityTrigger] string json, FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger("SaveOrderToBlob");
        
        string connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        string containerName = Environment.GetEnvironmentVariable("OrderBlobContainer");

        string orderId;
        try
        {
            using var jsonDoc = JsonDocument.Parse(json);
            orderId = jsonDoc.RootElement.GetProperty("OrderId").GetInt32().ToString();
        }
        catch (Exception ex) {
            logger.LogError("OrderId not found. Using GUID");
            orderId = Guid.NewGuid().ToString();
        }

        string blobName = $"order-{orderId}.json";

        var blobClient = new BlobContainerClient(connectionString, containerName);
        await blobClient.CreateIfNotExistsAsync();
        var blob = blobClient.GetBlobClient(blobName);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await blob.UploadAsync(stream, overwrite: true);

        logger.LogInformation($"Saved blob: {blobName}");

        return $"Saved {blobName}";
    }

    [Function("HttpStart")]
    public static async Task<HttpResponseData> HttpStart(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestData req,
        [DurableClient] DurableTaskClient client,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger("HttpStart");

        string jsonContent = await req.ReadAsStringAsync();

        // Function input comes from the request content.
        string instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
            "Function1", input: jsonContent);

        logger.LogInformation("Started orchestration with ID = '{instanceId}'.", instanceId);

        // Returns an HTTP 202 response with an instance management payload.
        // See https://learn.microsoft.com/azure/azure-functions/durable/durable-functions-http-api#start-orchestration
        return await client.CreateCheckStatusResponseAsync(req, instanceId);
    }
}