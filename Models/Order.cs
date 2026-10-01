namespace OrderProcessing.Models;

public class Order
{
    public string OrderId { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public List<OrderItem> Items { get; set; } = new();
    public decimal TotalAmount { get; set; }
    public DateTime OrderDate { get; set; }
    public OrderStatus Status { get; set; }
}

public class OrderItem
{
    public string ProductId { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal Price { get; set; }
}

public enum OrderStatus
{
    Pending,
    Validated,
    PaymentProcessed,
    EmailSent,
    Completed,
    Failed,
    // Appended so existing numeric values (e.g. Completed = 4) do not change.
    InventoryReserved,
    Cancelled
}

public class OrderResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public Order? Order { get; set; }

    public static OrderResult Ok(string message, Order? order = null) => new() { Success = true, Message = message, Order = order };
    public static OrderResult Fail(string message, Order? order = null) => new() { Success = false, Message = message, Order = order };
}
