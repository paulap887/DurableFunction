using OrderProcessing.Models;

namespace OrderProcessing.Tests;

public static class TestData
{
    public static Order ValidOrder(string? orderId = null) => new()
    {
        OrderId = orderId ?? Guid.NewGuid().ToString(),
        CustomerName = "Jane Smith",
        CustomerEmail = "jane@example.com",
        Items =
        [
            new OrderItem { ProductId = "PROD001", ProductName = "Laptop", Quantity = 1, Price = 999.99m },
            new OrderItem { ProductId = "PROD002", ProductName = "Wireless Mouse", Quantity = 2, Price = 29.99m },
        ],
        TotalAmount = 1059.97m,
    };
}
