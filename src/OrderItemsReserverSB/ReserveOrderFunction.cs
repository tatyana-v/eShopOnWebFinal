using System.Text;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Polly;

namespace OrderItemsReserverSB;

public class ReserveOrderFunction
{
    private readonly BlobContainerClient _container;
    private readonly HttpClient _http;
    private readonly ILogger<ReserveOrderFunction> _logger;
    private readonly string _logicAppUrl;

    public ReserveOrderFunction(IConfiguration cfg, ILogger<ReserveOrderFunction> logger)
    {
        _logger = logger;
        _http = new HttpClient();

        var blobConn = cfg["BlobConnection"];
        var containerName = cfg["ContainerName"] ?? "orders";
        _container = new BlobContainerClient(blobConn, containerName);
        //_container.CreateIfNotExists();

        _logicAppUrl = cfg["LogicAppUrl"] ?? "";
    }

    [Function(nameof(ReserveOrderFunction))]
    public async Task Run(
        [ServiceBusTrigger("order-items-reserver", Connection = "ServiceBusConnection")]
        ServiceBusReceivedMessage message)
    {
        _logger.LogInformation("Message ID: {id}", message.MessageId);
        _logger.LogInformation("Message Body: {body}", message.Body);
        _logger.LogInformation("Message Content-Type: {contentType}", message.ContentType);

        int orderId = 0;
        try
        {
            using var doc = JsonDocument.Parse(message.Body);
            orderId = doc.RootElement.GetProperty("OrderId").GetInt32();
        }
        catch
        {
            orderId = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        var blobName = $"{orderId}.json";
        var blob = _container.GetBlobClient(blobName);

        var retry = Policy
            .Handle<Exception>()
            .WaitAndRetryAsync(3, i => TimeSpan.FromSeconds(Math.Pow(2, i)),
                (ex, ts, i, _) => _logger.LogWarning(ex, "Retry {Try} uploading blob {Blob}", i, blobName));

        try
        {
            await retry.ExecuteAsync(async () =>
            {
                using var ms = new MemoryStream(message.Body.ToArray());
                await blob.UploadAsync(ms, overwrite: true);
            });

            _logger.LogInformation("Order {OrderId} stored as blob {BlobName}.", orderId, blobName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload blob for Order {OrderId}. Fallback to Logic App.", orderId);



            if (!string.IsNullOrWhiteSpace(_logicAppUrl))
            {
                var payload = new
                {
                    OrderId = orderId,
                    ExceptionMessage = ex.Message,
                    ExceptionType = ex.GetType().FullName,
                    OccurredAtUtc = DateTime.UtcNow
                };
                var json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(JsonSerializer.Serialize(json), Encoding.UTF8, "application/json");
                var resp = await _http.PostAsync(_logicAppUrl, content);

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Logic App returned non-success status: {Status} {Reason}",
                        (int)resp.StatusCode, resp.ReasonPhrase);
                }
                else
                {
                    _logger.LogInformation("Logic App successfully processed Order {OrderId} fallback.", orderId);
                }
            }
            throw;
        }

    }
}
