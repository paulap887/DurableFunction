using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using OrderProcessing.Models;
using System.Net;
using System.Text.Json;

namespace OrderProcessing.Functions;

public class OrderHttpTrigger
{
    private readonly ILogger<OrderHttpTrigger> _logger;

    public OrderHttpTrigger(ILogger<OrderHttpTrigger> logger)
    {
        _logger = logger;
    }

    [Function(nameof(StartOrderProcessing))]
    public async Task<HttpResponseData> StartOrderProcessing(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "orders")] HttpRequestData req,
        [DurableClient] DurableTaskClient client)
    {
        _logger.LogInformation("Received new order processing request");

        try
        {
            // Read and parse the request body
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var order = JsonSerializer.Deserialize<Order>(requestBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (order == null)
            {
                _logger.LogWarning("Invalid order data received");
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteStringAsync("Invalid order data");
                return badRequest;
            }

            // Generate order ID if not provided
            if (string.IsNullOrEmpty(order.OrderId))
            {
                order.OrderId = Guid.NewGuid().ToString();
            }

            // Set order date
            order.OrderDate = DateTime.UtcNow;
            order.Status = OrderStatus.Pending;

            // Start the orchestration
            string instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
                nameof(OrderOrchestrator.RunOrderOrchestration),
                order);

            _logger.LogInformation($"Started orchestration with ID = '{instanceId}' for order {order.OrderId}");

            // Return the orchestration status
            var response = req.CreateResponse(HttpStatusCode.Accepted);
            var result = new
            {
                orderId = order.OrderId,
                instanceId = instanceId,
                statusQueryGetUri = $"{req.Url.Scheme}://{req.Url.Authority}/runtime/webhooks/durabletask/instances/{instanceId}",
                message = "Order processing started successfully"
            };

            await response.WriteAsJsonAsync(result);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing order request");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync($"Error: {ex.Message}");
            return errorResponse;
        }
    }

    [Function(nameof(GetOrderStatus))]
    public async Task<HttpResponseData> GetOrderStatus(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "orders/{instanceId}")] HttpRequestData req,
        [DurableClient] DurableTaskClient client,
        string instanceId)
    {
        _logger.LogInformation($"Getting status for orchestration instance: {instanceId}");

        try
        {
            var metadata = await client.GetInstanceAsync(instanceId, getInputsAndOutputs: true);

            if (metadata == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteStringAsync($"No instance found with ID: {instanceId}");
                return notFound;
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            var status = new
            {
                instanceId = metadata.InstanceId,
                runtimeStatus = metadata.RuntimeStatus.ToString(),
                createdTime = metadata.CreatedAt,
                lastUpdatedTime = metadata.LastUpdatedAt,
                output = metadata.ReadOutputAs<OrderResult>()
            };

            await response.WriteAsJsonAsync(status);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error getting status for instance {instanceId}");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync($"Error: {ex.Message}");
            return errorResponse;
        }
    }

    [Function(nameof(CreateSampleOrder))]
    public async Task<HttpResponseData> CreateSampleOrder(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "orders/sample")] HttpRequestData req)
    {
        _logger.LogInformation("Creating sample order");

        var sampleOrder = new Order
        {
            CustomerName = "John Doe",
            CustomerEmail = "john.doe@example.com",
            Items = new List<OrderItem>
            {
                new OrderItem
                {
                    ProductId = "PROD001",
                    ProductName = "Laptop",
                    Quantity = 1,
                    Price = 999.99m
                },
                new OrderItem
                {
                    ProductId = "PROD002",
                    ProductName = "Wireless Mouse",
                    Quantity = 2,
                    Price = 29.99m
                }
            },
            TotalAmount = 1059.97m
        };

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(sampleOrder);
        return response;
    }
}
