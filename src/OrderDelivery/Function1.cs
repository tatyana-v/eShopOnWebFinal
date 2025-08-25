using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.Cosmos;
using System.Net;
using Grpc.Core;

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

            var delaySetting = Environment.GetEnvironmentVariable("OrderSaveDelaySeconds");

            

            using var cosmosClient = new CosmosClient(connectionString);
            var container = cosmosClient.GetContainer(databaseId, containerId);

            try
            {
                if (int.TryParse(delaySetting, out var delaySeconds) && delaySeconds > 0)
                {
                    logger.LogInformation($"Set delay for {delaySeconds} seconds.");
                    await Task.Delay(delaySeconds*1000);
                }
                var response = await container.CreateItemAsync(order, new PartitionKey(order.OrderId));
                logger.LogInformation($"Order {order.OrderId} saved to CosmosDB. Status: {response.StatusCode}");
                return $"Order {order.OrderId} saved.";
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                var msg = $"Order {order.OrderId} already exists in CosmosDB";
                logger.LogInformation(msg);
                return msg;
            }

        }

        [Function("HttpStart")]
        public static async Task<HttpResponseData> HttpStart(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequestData req,
            [DurableClient] DurableTaskClient client,
            FunctionContext executionContext)
        {
            var logger = executionContext.GetLogger("HttpStart");

            // Read order data from request body
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var orderData = JsonSerializer.Deserialize<OrderData>(requestBody);

            // Pass order data to orchestrator
            //string instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
            //    nameof(Function1), orderData);

            string instanceId = orderData.OrderId.ToString();

            var meta = await client.GetInstanceAsync(instanceId, getInputsAndOutputs: false);

            var waitSetting = Environment.GetEnvironmentVariable("OrderStatusMaxWaitSeconds");
            var intervalSetting = Environment.GetEnvironmentVariable("OrderStatusIntervalSeconds");

            int.TryParse(waitSetting, out int waitSeconds);
            if(waitSeconds < 0) waitSeconds = 0;
            int.TryParse(intervalSetting, out int intervalSeconds);
            if (intervalSeconds < 1) intervalSeconds = 1;

            async Task<OrchestrationMetadata?> WaitForCompletion(string id, int maxWaitSeconds, int checkIntervalSeconds)
            {
                if (maxWaitSeconds <= 0)
                {
                    return await client.GetInstanceAsync(id);
                }

                var deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);
                OrchestrationMetadata? meta = null;
                
                while (DateTime.UtcNow < deadline)
                {
                    meta = await client.GetInstanceAsync(id, getInputsAndOutputs: false);
                    if (meta is null) return null;

                    if (meta.RuntimeStatus is not (OrchestrationRuntimeStatus.Pending or OrchestrationRuntimeStatus.Running))
                    {
                        return meta;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(checkIntervalSeconds));
                }

                return meta ?? await client.GetInstanceAsync(id, getInputsAndOutputs: false);

                /**using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(maxWaitSeconds));

                try
                {
                    return await client.WaitForInstanceCompletionAsync(id, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return await client.GetInstanceAsync(id);
                }**/
            }

            if (meta == null)
            {
                var options = new StartOrchestrationOptions(instanceId);
                await client.ScheduleNewOrchestrationInstanceAsync(nameof(Function1), orderData, options);

                logger.LogInformation("Started new orchestration '{InstanceId}' for orderId {OrderId}.", instanceId, orderData.OrderId);

                return await client.CreateCheckStatusResponseAsync(req, instanceId);
            }

            switch (meta.RuntimeStatus)
            {
                case OrchestrationRuntimeStatus.Completed:
                    {
                        logger.LogInformation("Order {.OrderId} already processed (instance {InstanceId}). No new record created.", orderData.OrderId, instanceId);
                        var ok = req.CreateResponse(HttpStatusCode.OK);
                        await ok.WriteStringAsync($"Order {orderData.OrderId} already added. No new record created.");
                        return ok;
                    }
                case OrchestrationRuntimeStatus.Running:
                case OrchestrationRuntimeStatus.Pending:
                    {
                        logger.LogInformation("Order {OrderId} is being processed (status {Status}). Waiting for completion...", orderData.OrderId, meta.RuntimeStatus);
                        var final = await WaitForCompletion(instanceId,waitSeconds, intervalSeconds);
                        if (final?.RuntimeStatus is OrchestrationRuntimeStatus.Completed)
                        {
                            var ok = req.CreateResponse(HttpStatusCode.OK);
                            await ok.WriteStringAsync($"Order {orderData.OrderId} processed while waiting.");
                            return ok;
                        }

                        logger.LogInformation("Order {OrderId} still processing. Returning status URL.", orderData.OrderId);
                        return await client.CreateCheckStatusResponseAsync(req, instanceId);
                    }

                case OrchestrationRuntimeStatus.Failed:
                case OrchestrationRuntimeStatus.Terminated:
                    {
                        logger.LogWarning("Order {OrderID} previously failed (status {Status}). Trying once again...", orderData.OrderId, meta.RuntimeStatus);

                        await client.PurgeInstanceAsync(instanceId);

                        var options = new StartOrchestrationOptions(instanceId);
                        await client.ScheduleNewOrchestrationInstanceAsync(nameof(Function1), orderData, options);

                        var afterRetry = await WaitForCompletion(instanceId, waitSeconds, intervalSeconds);

                        if (afterRetry?.RuntimeStatus == OrchestrationRuntimeStatus.Completed)
                        {
                            logger.LogInformation($"Retry for order {orderData.OrderId} succeeded.");
                            var ok = req.CreateResponse(HttpStatusCode.OK);
                            await ok.WriteStringAsync($"Order {orderData.OrderId} saved on retry.");
                            return ok;
                        }

                        logger.LogError($"Retry for order {orderData.OrderId} did not succeed.");
                        var err = req.CreateResponse(HttpStatusCode.InternalServerError);
                        await err.WriteStringAsync($"Order {orderData.OrderId} could not be saved. Retry failed.");
                        return err;

                    }
                default:
                    {
                        var resp = req.CreateResponse(HttpStatusCode.Accepted);
                        await resp.WriteStringAsync($"Order {orderData.OrderId}: current status = {meta.RuntimeStatus}.");
                        return resp;
                    }
            }
        }
    }
}
