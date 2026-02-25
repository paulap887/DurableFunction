using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using OrderProcessing.Models;

namespace OrderProcessing.Functions;

public class OrderOrchestrator
{
    [Function(nameof(RunOrderOrchestration))]
    public async Task<OrderResult> RunOrderOrchestration(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        ILogger logger = context.CreateReplaySafeLogger(nameof(OrderOrchestrator));

        var order = context.GetInput<Order>()!;
        logger.LogInformation($"Starting order orchestration for Order ID: {order.OrderId}");

        try
        {
            // Step 1: Validate the order
            logger.LogInformation("Step 1: Validating order");
            var validationResult = await context.CallActivityAsync<OrderResult>(
                nameof(OrderActivities.ValidateOrder),
                order);

            if (!validationResult.Success)
            {
                logger.LogWarning($"Order validation failed: {validationResult.Message}");
                return new OrderResult
                {
                    Success = false,
                    Message = validationResult.Message,
                    Order = order
                };
            }

            order.Status = OrderStatus.Validated;
            logger.LogInformation("Order validated successfully");

            // Step 2: Process payment
            logger.LogInformation("Step 2: Processing payment");
            var paymentResult = await context.CallActivityAsync<OrderResult>(
                nameof(OrderActivities.ProcessPayment),
                order);

            if (!paymentResult.Success)
            {
                logger.LogWarning($"Payment processing failed: {paymentResult.Message}");
                return new OrderResult
                {
                    Success = false,
                    Message = paymentResult.Message,
                    Order = order
                };
            }

            order.Status = OrderStatus.PaymentProcessed;
            logger.LogInformation("Payment processed successfully");

            // Step 3: Send confirmation email
            logger.LogInformation("Step 3: Sending confirmation email");
            var emailResult = await context.CallActivityAsync<OrderResult>(
                nameof(OrderActivities.SendConfirmationEmail),
                order);

            if (!emailResult.Success)
            {
                logger.LogWarning($"Email sending failed: {emailResult.Message}");
                // Don't fail the entire order if email fails
                order.Status = OrderStatus.Completed;
                return new OrderResult
                {
                    Success = true,
                    Message = "Order completed but email notification failed",
                    Order = order
                };
            }

            order.Status = OrderStatus.Completed;
            logger.LogInformation($"Order orchestration completed successfully for Order ID: {order.OrderId}");

            return new OrderResult
            {
                Success = true,
                Message = "Order processed successfully",
                Order = order
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error processing order {order.OrderId}");
            order.Status = OrderStatus.Failed;
            return new OrderResult
            {
                Success = false,
                Message = $"Order processing failed: {ex.Message}",
                Order = order
            };
        }
    }
}
