using System.Collections.Concurrent;
using OrderProcessing.Models;

namespace OrderProcessing.Services;

public interface IInventoryService
{
    /// <summary>Reserves stock for every item, or nothing. Reserving the same order twice is a no-op.</summary>
    bool TryReserve(string orderId, IReadOnlyList<OrderItem> items, out string reason);

    /// <summary>Returns an order's reserved stock. Releasing an unknown or already released order is a no-op.</summary>
    void Release(string orderId);

    int Available(string productId);
}

/// <summary>
/// Stands in for a real inventory system. Keeps stock in memory, so it only works for a single local instance.
/// </summary>
public class InMemoryInventoryService(int initialStockPerProduct = 100) : IInventoryService
{
    private readonly object _lock = new();
    private readonly Dictionary<string, int> _stock = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<OrderItem>> _reservations = new();

    public bool TryReserve(string orderId, IReadOnlyList<OrderItem> items, out string reason)
    {
        lock (_lock)
        {
            if (_reservations.ContainsKey(orderId))
            {
                reason = string.Empty;
                return true;
            }

            var shortItem = items.FirstOrDefault(i => AvailableLocked(i.ProductId) < i.Quantity);
            if (shortItem is not null)
            {
                reason = $"Not enough stock for {shortItem.ProductName} ({shortItem.ProductId}): requested {shortItem.Quantity}, available {AvailableLocked(shortItem.ProductId)}";
                return false;
            }

            foreach (var item in items)
                _stock[item.ProductId] = AvailableLocked(item.ProductId) - item.Quantity;
            _reservations[orderId] = items;
            reason = string.Empty;
            return true;
        }
    }

    public void Release(string orderId)
    {
        lock (_lock)
        {
            if (!_reservations.TryRemove(orderId, out var items))
                return;
            foreach (var item in items)
                _stock[item.ProductId] = AvailableLocked(item.ProductId) + item.Quantity;
        }
    }

    public int Available(string productId)
    {
        lock (_lock)
            return AvailableLocked(productId);
    }

    private int AvailableLocked(string productId) =>
        _stock.TryGetValue(productId, out var available) ? available : initialStockPerProduct;
}
