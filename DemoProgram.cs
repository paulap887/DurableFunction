using OrderProcessing.Models;
using Microsoft.Extensions.Logging;

namespace OrderProcessing.Demo;

// Standalone demo to show the workflow without Azure Functions runtime
public class DemoProgram
{
    public static async Task RunDemo(string[] args)
    {
        Console.WriteLine("========================================");
        Console.WriteLine("  Azure Durable Functions - Order Processing Demo");
        Console.WriteLine("========================================\n");

        // Create a sample order
        var order = new Order
        {
            OrderId = Guid.NewGuid().ToString(),
            CustomerName = "John Doe",
            CustomerEmail = "john.doe@example.com",
            OrderDate = DateTime.UtcNow,
            Status = OrderStatus.Pending,
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

        Console.WriteLine($"Order ID: {order.OrderId}");
        Console.WriteLine($"Customer: {order.CustomerName} ({order.CustomerEmail})");
        Console.WriteLine($"Total Amount: ${order.TotalAmount}\n");
        Console.WriteLine("Items:");
        foreach (var item in order.Items)
        {
            Console.WriteLine($"  - {item.ProductName} x{item.Quantity} @ ${item.Price} = ${item.Price * item.Quantity}");
        }
        Console.WriteLine("\n========================================");
        Console.WriteLine("Starting Order Processing Workflow");
        Console.WriteLine("========================================\n");

        // Simulate the orchestration workflow
        var activities = new DemoOrderActivities();

        try
        {
            // Step 1: Validate Order
            Console.WriteLine("Step 1: Validating Order...");
            var validationResult = activities.ValidateOrder(order);

            if (!validationResult.Success)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"❌ Validation Failed: {validationResult.Message}");
                Console.ResetColor();
                return;
            }

            order.Status = OrderStatus.Validated;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✓ {validationResult.Message}");
            Console.ResetColor();
            await Task.Delay(1000);

            // Step 2: Process Payment
            Console.WriteLine("\nStep 2: Processing Payment...");
            var paymentResult = await activities.ProcessPayment(order);

            if (!paymentResult.Success)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"❌ Payment Failed: {paymentResult.Message}");
                Console.ResetColor();
                return;
            }

            order.Status = OrderStatus.PaymentProcessed;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✓ {paymentResult.Message}");
            Console.ResetColor();
            await Task.Delay(1000);

            // Step 3: Send Confirmation Email
            Console.WriteLine("\nStep 3: Sending Confirmation Email...");
            var emailResult = await activities.SendConfirmationEmail(order);

            if (!emailResult.Success)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"⚠ Email Warning: {emailResult.Message}");
                Console.ResetColor();
                Console.WriteLine("Order completed but email notification failed");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"✓ {emailResult.Message}");
                Console.ResetColor();
            }

            order.Status = OrderStatus.Completed;

            // Final Summary
            Console.WriteLine("\n========================================");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✓ ORDER PROCESSING COMPLETED SUCCESSFULLY");
            Console.ResetColor();
            Console.WriteLine("========================================");
            Console.WriteLine($"\nOrder ID: {order.OrderId}");
            Console.WriteLine($"Final Status: {order.Status}");
            Console.WriteLine($"Processed at: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n❌ ERROR: {ex.Message}");
            Console.ResetColor();
            order.Status = OrderStatus.Failed;
        }
    }
}

// Simplified activity implementations for demo
public class DemoOrderActivities
{
    public OrderResult ValidateOrder(Order order)
    {
        // Validate customer information
        if (string.IsNullOrWhiteSpace(order.CustomerName))
        {
            return new OrderResult { Success = false, Message = "Customer name is required" };
        }

        if (string.IsNullOrWhiteSpace(order.CustomerEmail) || !order.CustomerEmail.Contains('@'))
        {
            return new OrderResult { Success = false, Message = "Valid customer email is required" };
        }

        // Validate order items
        if (order.Items == null || order.Items.Count == 0)
        {
            return new OrderResult { Success = false, Message = "Order must contain at least one item" };
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

        return new OrderResult { Success = true, Message = "Order validation successful" };
    }

    public async Task<OrderResult> ProcessPayment(Order order)
    {
        // Simulate payment processing delay
        await Task.Delay(2000);

        // Note: Random failures disabled for demo to ensure success
        Console.WriteLine($"  Processing payment of ${order.TotalAmount}...");
        Console.WriteLine("  Connecting to payment gateway...");
        await Task.Delay(500);
        Console.WriteLine("  Authorizing transaction...");
        await Task.Delay(500);

        return new OrderResult
        {
            Success = true,
            Message = $"Payment of ${order.TotalAmount} processed successfully"
        };
    }

    public async Task<OrderResult> SendConfirmationEmail(Order order)
    {
        // Simulate email sending delay
        await Task.Delay(1000);

        Console.WriteLine($"  Composing email to {order.CustomerEmail}...");
        await Task.Delay(300);
        Console.WriteLine("  Connecting to email service...");
        await Task.Delay(300);
        Console.WriteLine("  Email sent!");

        return new OrderResult { Success = true, Message = "Confirmation email sent successfully" };
    }
}
