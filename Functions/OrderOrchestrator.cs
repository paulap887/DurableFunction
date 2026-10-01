using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using OrderProcessing.Models;

namespace OrderProcessing.Functions;

/// <summary>
/// Saga: Validate -> ReserveInventory -> ProcessPayment -> SendConfirmationEmail.
/// If payment is declined, the inventory reservation is compensated (released). If payment keeps failing, its outcome
/// is unknown (a charge can succeed and then time out), so the payment is refunded as well.
/// </summary>
public class OrderOrchestrator
{
    // Transient failures (the activity throws) are retried with exponential backoff: 2s, 4s, 8s.
    // Business outcomes (the activity returns Success = false) are not retried.
    public static readonly TaskOptions PaymentRetry = TaskOptions.FromRetryPolicy(new RetryPolicy(
        maxNumberOfAttempts: 4,
        firstRetryInterval: TimeSpan.FromSeconds(2),
        backoffCoefficient: 2.0));

    public static readonly TaskOptions EmailRetry = TaskOptions.FromRetryPolicy(new RetryPolicy(
        maxNumberOfAttempts: 3,
        firstRetryInterval: TimeSpan.FromSeconds(5)));

    // Compensation must eventually succeed, so it gets the most patient policy.
    public static readonly TaskOptions CompensationRetry = TaskOptions.FromRetryPolicy(new RetryPolicy(
        maxNumberOfAttempts: 10,
        firstRetryInterval: TimeSpan.FromSeconds(5),
        backoffCoefficient: 2.0,
        maxRetryInterval: TimeSpan.FromMinutes(5)));

    [Function(nameof(RunOrderOrchestration))]
    public async Task<OrderResult> RunOrderOrchestration(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        ILogger logger = context.CreateReplaySafeLogger(nameof(OrderOrchestrator));

        var order = context.GetInput<Order>()!;
        logger.LogInformation("Starting order orchestration for order {OrderId}", order.OrderId);

        // Step 1: Validate the order
        context.SetCustomStatus(new { step = "Validating" });
        var validationResult = await context.CallActivityAsync<OrderResult>(nameof(OrderActivities.ValidateOrder), order);
        if (!validationResult.Success)
            return Finish(logger, order, OrderStatus.Failed, validationResult.Message);
        order.Status = OrderStatus.Validated;

        // Step 2: Reserve inventory
        context.SetCustomStatus(new { step = "ReservingInventory" });
        var reservation = await context.CallActivityAsync<OrderResult>(nameof(OrderActivities.ReserveInventory), order);
        if (!reservation.Success)
            return Finish(logger, order, OrderStatus.Failed, reservation.Message);
        order.Status = OrderStatus.InventoryReserved;

        // Step 3: Process payment (retried on transient failures, compensated if it does not succeed)
        context.SetCustomStatus(new { step = "ProcessingPayment" });
        OrderResult paymentResult;
        try
        {
            paymentResult = await context.CallActivityAsync<OrderResult>(nameof(OrderActivities.ProcessPayment), order, PaymentRetry);
        }
        catch (TaskFailedException ex)
        {
            logger.LogError("Payment for order {OrderId} failed after all retries: {Error}", order.OrderId, ex.FailureDetails.ErrorMessage);
            await CompensateAsync(context, order, refundPayment: true);
            return Finish(logger, order, OrderStatus.Cancelled, $"Payment service unavailable, order cancelled: {ex.FailureDetails.ErrorMessage}");
        }

        if (!paymentResult.Success)
        {
            await CompensateAsync(context, order, refundPayment: false); // declined: nothing was charged
            return Finish(logger, order, OrderStatus.Cancelled, paymentResult.Message);
        }
        order.Status = OrderStatus.PaymentProcessed;

        // Step 4: Send confirmation email. The order is already paid, so an email failure must not undo it.
        context.SetCustomStatus(new { step = "SendingConfirmation" });
        try
        {
            await context.CallActivityAsync<OrderResult>(nameof(OrderActivities.SendConfirmationEmail), order, EmailRetry);
        }
        catch (TaskFailedException ex)
        {
            logger.LogWarning("Confirmation email for order {OrderId} failed after all retries: {Error}", order.OrderId, ex.FailureDetails.ErrorMessage);
            return Finish(logger, order, OrderStatus.Completed, "Order completed but email notification failed", success: true);
        }

        return Finish(logger, order, OrderStatus.Completed, "Order processed successfully", success: true);
    }

    /// <summary>Undoes completed steps in reverse order.</summary>
    private static async Task CompensateAsync(TaskOrchestrationContext context, Order order, bool refundPayment)
    {
        context.SetCustomStatus(new { step = "Compensating" });
        if (refundPayment)
            await context.CallActivityAsync(nameof(OrderActivities.RefundPayment), order, CompensationRetry);
        await context.CallActivityAsync(nameof(OrderActivities.ReleaseInventory), order, CompensationRetry);
    }

    private static OrderResult Finish(ILogger logger, Order order, OrderStatus status, string message, bool success = false)
    {
        order.Status = status;
        logger.LogInformation("Order {OrderId} finished with status {Status}: {Message}", order.OrderId, status, message);
        return new OrderResult { Success = success, Message = message, Order = order };
    }
}
