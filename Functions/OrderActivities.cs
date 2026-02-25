using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using OrderProcessing.Models;

namespace OrderProcessing.Functions;

public class OrderActivities
{
    private readonly ILogger<OrderActivities> _logger;

    public OrderActivities(ILogger<OrderActivities> logger)
    {
        _logger = logger;
    }

    [Function(nameof(ValidateOrder))]
    public OrderResult ValidateOrder([ActivityTrigger] Order order)
    {
        _logger.LogInformation($"Validating order: {order.OrderId}");

        // Validate customer information
        if (string.IsNullOrWhiteSpace(order.CustomerName))
        {
            return new OrderResult
            {
                Success = false,
                Message = "Customer name is required"
            };
        }

        if (string.IsNullOrWhiteSpace(order.CustomerEmail) || !order.CustomerEmail.Contains("@"))
        {
            return new OrderResult
            {
                Success = false,
                Message = "Valid customer email is required"
            };
        }

        // Validate order items
        if (order.Items == null || order.Items.Count == 0)
        {
            return new OrderResult
            {
                Success = false,
                Message = "Order must contain at least one item"
            };
        }

        // Validate total amount
        decimal calculatedTotal = order.Items.Sum(item => item.Price * item.Quantity);
        if (Math.Abs(order.TotalAmount - calculatedTotal) > 0.01m)
        {
            return new OrderResult
            {
                Success = false,
                Message = $"Order total mismatch. Expected: {calculatedTotal}, Got: {order.TotalAmount}"
            };
        }

        // Check for negative quantities or prices
        if (order.Items.Any(item => item.Quantity <= 0 || item.Price < 0))
        {
            return new OrderResult
            {
                Success = false,
                Message = "Invalid item quantity or price"
            };
        }

        _logger.LogInformation($"Order {order.OrderId} validated successfully");
        return new OrderResult
        {
            Success = true,
            Message = "Order validation successful"
        };
    }

    [Function(nameof(ProcessPayment))]
    public async Task<OrderResult> ProcessPayment([ActivityTrigger] Order order)
    {
        _logger.LogInformation($"Processing payment for order: {order.OrderId}, Amount: ${order.TotalAmount}");

        try
        {
            // Simulate payment processing delay
            await Task.Delay(2000);

            // In a real-world scenario, you would integrate with a payment gateway like Stripe, PayPal, etc.
            // For this example, we'll simulate a successful payment

            // Simulate random payment failures for demonstration (10% failure rate)
            Random random = new Random();
            if (random.Next(100) < 10)
            {
                _logger.LogWarning($"Payment declined for order {order.OrderId}");
                return new OrderResult
                {
                    Success = false,
                    Message = "Payment was declined by the payment processor"
                };
            }

            _logger.LogInformation($"Payment processed successfully for order {order.OrderId}");
            return new OrderResult
            {
                Success = true,
                Message = $"Payment of ${order.TotalAmount} processed successfully"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error processing payment for order {order.OrderId}");
            return new OrderResult
            {
                Success = false,
                Message = $"Payment processing error: {ex.Message}"
            };
        }
    }

    [Function(nameof(SendConfirmationEmail))]
    public async Task<OrderResult> SendConfirmationEmail([ActivityTrigger] Order order)
    {
        _logger.LogInformation($"Sending confirmation email to {order.CustomerEmail} for order {order.OrderId}");

        try
        {
            // Simulate email sending delay
            await Task.Delay(1000);

            // In a real-world scenario, you would use SendGrid, Azure Communication Services, or similar
            // For this example, we'll just log the email content

            string emailSubject = $"Order Confirmation - {order.OrderId}";
            string emailBody = BuildEmailBody(order);

            _logger.LogInformation($"Email would be sent with subject: {emailSubject}");
            _logger.LogInformation($"Email body:\n{emailBody}");

            // Simulate successful email sending
            _logger.LogInformation($"Confirmation email sent successfully to {order.CustomerEmail}");

            return new OrderResult
            {
                Success = true,
                Message = "Confirmation email sent successfully"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error sending email for order {order.OrderId}");
            return new OrderResult
            {
                Success = false,
                Message = $"Email sending error: {ex.Message}"
            };
        }
    }

    private string BuildEmailBody(Order order)
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
