using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using OrderProcessing.Models;
using OrderProcessing.Services;

namespace OrderProcessing.Functions;

/// <summary>
/// Activities return <see cref="OrderResult"/> with Success = false for business outcomes (invalid order, out of stock,
/// card declined), which retrying would not change. They let transient failures throw, so the orchestrator's retry
/// policy can handle them. Catching every exception and returning Success = false would make retries impossible.
/// </summary>
public class OrderActivities(
    ILogger<OrderActivities> logger,
    IPaymentGateway paymentGateway,
    IInventoryService inventory)
{
    [Function(nameof(ValidateOrder))]
    public OrderResult ValidateOrder([ActivityTrigger] Order order)
    {
        logger.LogInformation("Validating order {OrderId}", order.OrderId);

        if (string.IsNullOrWhiteSpace(order.CustomerName))
            return OrderResult.Fail("Customer name is required");

        if (string.IsNullOrWhiteSpace(order.CustomerEmail) || !order.CustomerEmail.Contains('@'))
            return OrderResult.Fail("Valid customer email is required");

        if (order.Items == null || order.Items.Count == 0)
            return OrderResult.Fail("Order must contain at least one item");

        if (order.Items.Any(item => item.Quantity <= 0 || item.Price < 0))
            return OrderResult.Fail("Invalid item quantity or price");

        decimal calculatedTotal = order.Items.Sum(item => item.Price * item.Quantity);
        if (Math.Abs(order.TotalAmount - calculatedTotal) > 0.01m)
            return OrderResult.Fail($"Order total mismatch. Expected: {calculatedTotal}, Got: {order.TotalAmount}");

        logger.LogInformation("Order {OrderId} validated", order.OrderId);
        return OrderResult.Ok("Order validation successful");
    }

    [Function(nameof(ReserveInventory))]
    public OrderResult ReserveInventory([ActivityTrigger] Order order)
    {
        if (!inventory.TryReserve(order.OrderId, order.Items, out var reason))
        {
            logger.LogWarning("Inventory reservation failed for order {OrderId}: {Reason}", order.OrderId, reason);
            return OrderResult.Fail(reason);
        }

        logger.LogInformation("Inventory reserved for order {OrderId}", order.OrderId);
        return OrderResult.Ok("Inventory reserved");
    }

    /// <summary>Compensation for <see cref="ReserveInventory"/>. Idempotent, so it is safe to retry.</summary>
    [Function(nameof(ReleaseInventory))]
    public void ReleaseInventory([ActivityTrigger] Order order)
    {
        inventory.Release(order.OrderId);
        logger.LogInformation("Compensation: released inventory for order {OrderId}", order.OrderId);
    }

    [Function(nameof(ProcessPayment))]
    public async Task<OrderResult> ProcessPayment([ActivityTrigger] Order order)
    {
        logger.LogInformation("Processing payment for order {OrderId}, amount {Amount}", order.OrderId, order.TotalAmount);

        // The order id is the idempotency key: if a previous attempt charged the card and then timed out,
        // the retry gets the original result instead of charging twice.
        // PaymentGatewayUnavailableException is deliberately not caught.
        var outcome = await paymentGateway.ChargeAsync(order.OrderId, order.TotalAmount);

        if (!outcome.Approved)
        {
            logger.LogWarning("Payment declined for order {OrderId}: {Message}", order.OrderId, outcome.Message);
            return OrderResult.Fail(outcome.Message);
        }

        logger.LogInformation("Payment approved for order {OrderId}, transaction {TransactionId}", order.OrderId, outcome.TransactionId);
        return OrderResult.Ok(outcome.Message);
    }

    /// <summary>
    /// Compensation for <see cref="ProcessPayment"/> when its outcome is unknown: a charge can succeed and then time out,
    /// so "payment failed after all retries" does not mean the customer was not charged. Idempotent, so it is safe to retry.
    /// </summary>
    [Function(nameof(RefundPayment))]
    public async Task RefundPayment([ActivityTrigger] Order order)
    {
        var refunded = await paymentGateway.RefundAsync(order.OrderId);
        logger.LogInformation(refunded
            ? "Compensation: refunded payment for order {OrderId}"
            : "Compensation: no charge to refund for order {OrderId}", order.OrderId);
    }

    [Function(nameof(SendConfirmationEmail))]
    public async Task<OrderResult> SendConfirmationEmail([ActivityTrigger] Order order)
    {
        logger.LogInformation("Sending confirmation email to {Email} for order {OrderId}", order.CustomerEmail, order.OrderId);

        // Simulate email sending delay.
        // In a real-world scenario, you would use SendGrid, Azure Communication Services, or similar,
        // and let its exceptions propagate so the orchestrator's retry policy applies.
        await Task.Delay(1000);

        string emailSubject = $"Order Confirmation - {order.OrderId}";
        string emailBody = BuildEmailBody(order);

        logger.LogInformation("Email would be sent with subject: {Subject}", emailSubject);
        logger.LogInformation("Email body:\n{Body}", emailBody);

        return OrderResult.Ok("Confirmation email sent successfully");
    }

    private static string BuildEmailBody(Order order)
    {
        var itemsList = string.Join("\n", order.Items.Select(item =>
            $"  - {item.ProductName} (x{item.Quantity}) - ${item.Price * item.Quantity}"));

        return $@"
Dear {order.CustomerName},

Thank you for your order! Your order has been confirmed and is being processed.

Order Details:
--------------
Order ID: {order.OrderId}
Order Date: {order.OrderDate:yyyy-MM-dd HH:mm:ss}

Items:
{itemsList}

Total Amount: ${order.TotalAmount}

We will send you another email when your order ships.

Thank you for your business!

Best regards,
The Order Processing Team
";
    }
}
