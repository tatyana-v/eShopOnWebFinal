using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.Cosmos;
using System.Net;

namespace OrderDelivery
{
    public class OrderData
    {
        public string id { get; set; }
        public int OrderId { get; set; } // Changed from string to int
        public IEnumerable<ItemData> Items { get; set; }
        public decimal FinalPrice { get; set; }
        public ShippingAddressData ShippingAddress { get; set; }
    }

    public class ItemData
    {
        public int ItemId { get; set; } // Changed from string to int
        public int Quantity { get; set; }
        public decimal Price { get; set; }
    }

    public class ShippingAddressData
    {
        public string Street { get; set; }
        public string City { get; set; }
        public string State { get; set; }
        public string Country { get; set; }
        public string ZipCode { get; set; }
    }

    public static class Function1
    {
        [Function(nameof(Function1))]
        public static async Task<List<string>> RunOrchestrator(
            [OrchestrationTrigger] TaskOrchestrationContext context)
        {
            var orderData = context.GetInput<OrderData>();
            var outputs = new List<string>();

            // Save order to CosmosDB
            outputs.Add(await context.CallActivityAsync<string>(nameof(SaveOrderToCosmosDb), orderData));

            return outputs;
        }

        [Function(nameof(SaveOrderToCosmosDb))]
        public static async Task<string> SaveOrderToCosmosDb([ActivityTrigger] OrderData order, FunctionContext executionContext)
        {
            var logger = executionContext.GetLogger("SaveOrderToCosmosDb");

            string connectionString = Environment.GetEnvironmentVariable("CosmosDbConnectionString");
            string databaseId = Environment.GetEnvironmentVariable("CosmosDbDatabaseId");
            string containerId = Environment.GetEnvironmentVariable("CosmosDbcontainerId");

            using var cosmosClient = new CosmosClient(connectionString);
            var container = cosmosClient.GetContainer(databaseId, containerId);

            var response = await container.CreateItemAsync(order, new PartitionKey(order.OrderId));
            logger.LogInformation($"Order {order.OrderId} saved to CosmosDB. Status: {response.StatusCode}");

            return $"Order {order.OrderId} saved.";
        }

        [Function("HttpStart")]
        public static async Task<HttpResponseData> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestData req,
            [DurableClient] DurableTaskClient client,
            FunctionContext executionContext)
        {
            var logger = executionContext.GetLogger("HttpStart");

            // Read order data from request body
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var orderData = JsonSerializer.Deserialize<OrderData>(requestBody);

            // Pass order data to orchestrator
            string instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
                nameof(Function1), orderData);

            logger.LogInformation("Started orchestration with ID = '{instanceId}'.", instanceId);

            return await client.CreateCheckStatusResponseAsync(req, instanceId);
        }
    }
}
