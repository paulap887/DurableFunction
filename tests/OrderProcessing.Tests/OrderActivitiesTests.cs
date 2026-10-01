using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OrderProcessing.Functions;
using OrderProcessing.Models;
using OrderProcessing.Services;

namespace OrderProcessing.Tests;

public class OrderActivitiesTests
{
    private readonly Mock<IPaymentGateway> _payments = new();
    private readonly InMemoryInventoryService _inventory = new(initialStockPerProduct: 5);
    private readonly OrderActivities _activities;

    public OrderActivitiesTests() =>
        _activities = new OrderActivities(NullLogger<OrderActivities>.Instance, _payments.Object, _inventory);

    [Fact]
    public void ValidateOrder_AcceptsValidOrder() =>
        Assert.True(_activities.ValidateOrder(TestData.ValidOrder()).Success);

    public static TheoryData<string, Action<Order>> InvalidOrders => new()
    {
        { "Customer name is required", o => o.CustomerName = "" },
        { "Valid customer email is required", o => o.CustomerEmail = "not-an-email" },
        { "Order must contain at least one item", o => o.Items.Clear() },
        { "Invalid item quantity or price", o => o.Items[0].Quantity = 0 },
        { "Invalid item quantity or price", o => o.Items[0].Price = -1 },
        { "Order total mismatch", o => o.TotalAmount = 1m },
    };

    [Theory]
    [MemberData(nameof(InvalidOrders))]
    public void ValidateOrder_RejectsInvalidOrder(string expectedMessage, Action<Order> breakOrder)
    {
        var order = TestData.ValidOrder();
        breakOrder(order);

        var result = _activities.ValidateOrder(order);

        Assert.False(result.Success);
        Assert.StartsWith(expectedMessage, result.Message);
    }

    [Fact]
    public async Task ProcessPayment_UsesOrderIdAsIdempotencyKey()
    {
        var order = TestData.ValidOrder("order-42");
        _payments.Setup(p => p.ChargeAsync("order-42", order.TotalAmount, default))
            .ReturnsAsync(new PaymentOutcome(true, "txn_1", "approved"));

        var result = await _activities.ProcessPayment(order);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ProcessPayment_Declined_ReturnsFailure_SoItIsNotRetried()
    {
        _payments.Setup(p => p.ChargeAsync(It.IsAny<string>(), It.IsAny<decimal>(), default))
            .ReturnsAsync(new PaymentOutcome(false, null, "declined"));

        var result = await _activities.ProcessPayment(TestData.ValidOrder());

        Assert.False(result.Success);
        Assert.Equal("declined", result.Message);
    }

    [Fact]
    public async Task ProcessPayment_TransientFailure_Throws_SoTheRetryPolicyApplies()
    {
        _payments.Setup(p => p.ChargeAsync(It.IsAny<string>(), It.IsAny<decimal>(), default))
            .ThrowsAsync(new PaymentGatewayUnavailableException("503"));

        await Assert.ThrowsAsync<PaymentGatewayUnavailableException>(() => _activities.ProcessPayment(TestData.ValidOrder()));
    }

    [Fact]
    public void ReserveInventory_ThenRelease_RestoresStock()
    {
        var order = TestData.ValidOrder();

        Assert.True(_activities.ReserveInventory(order).Success);
        Assert.Equal(3, _inventory.Available("PROD002"));

        _activities.ReleaseInventory(order);
        Assert.Equal(5, _inventory.Available("PROD002"));
    }

    [Fact]
    public void ReserveInventory_NotEnoughStock_ReservesNothing()
    {
        var order = TestData.ValidOrder();
        order.Items[1].Quantity = 6;

        var result = _activities.ReserveInventory(order);

        Assert.False(result.Success);
        Assert.Contains("Not enough stock for Wireless Mouse", result.Message);
        Assert.Equal(5, _inventory.Available("PROD001")); // all or nothing
    }
}

public class InMemoryInventoryServiceTests
{
    [Fact]
    public void ReserveAndRelease_AreIdempotent_SoActivityRetriesAreSafe()
    {
        var inventory = new InMemoryInventoryService(initialStockPerProduct: 10);
        var items = TestData.ValidOrder().Items;

        Assert.True(inventory.TryReserve("order-1", items, out _));
        Assert.True(inventory.TryReserve("order-1", items, out _)); // retried activity
        Assert.Equal(8, inventory.Available("PROD002"));

        inventory.Release("order-1");
        inventory.Release("order-1"); // retried compensation
        Assert.Equal(10, inventory.Available("PROD002"));
    }
}

public class SimulatedPaymentGatewayTests
{
    /// <summary>Random that returns a fixed sequence from NextDouble.</summary>
    private sealed class ScriptedRandom(params double[] values) : Random
    {
        private int _next;
        public override double NextDouble() => values[_next++ % values.Length];
    }

    private static SimulatedPaymentGateway Gateway(params double[] rolls) =>
        new(new SimulatedPaymentGatewayOptions { TransientFailureRate = 0.3, DeclineAbove = 5_000m }, new ScriptedRandom(rolls));

    [Fact]
    public async Task ApprovesNormalCharge()
    {
        var outcome = await Gateway(0.9).ChargeAsync("order-1", 100m);

        Assert.True(outcome.Approved);
        Assert.StartsWith("txn_", outcome.TransactionId);
    }

    [Fact]
    public async Task DeclinesChargeOverLimit()
    {
        var outcome = await Gateway(0.9).ChargeAsync("order-1", 5_000.01m);

        Assert.False(outcome.Approved);
    }

    [Fact]
    public async Task FailureBeforeCharging_Throws_AndRetryCharges()
    {
        var gateway = Gateway(0.1, 0.9); // first call fails before charging, second succeeds

        await Assert.ThrowsAsync<PaymentGatewayUnavailableException>(() => gateway.ChargeAsync("order-1", 100m));
        Assert.True((await gateway.ChargeAsync("order-1", 100m)).Approved);
    }

    [Fact]
    public async Task TimeoutAfterCharging_RetryReturnsOriginalCharge_InsteadOfChargingTwice()
    {
        var gateway = Gateway(0.2, 0.9); // first call charges and then times out

        await Assert.ThrowsAsync<PaymentGatewayUnavailableException>(() => gateway.ChargeAsync("order-1", 100m));
        var retry = await gateway.ChargeAsync("order-1", 100m);
        var anotherOrder = await gateway.ChargeAsync("order-2", 100m);

        Assert.True(retry.Approved);
        Assert.NotEqual(retry.TransactionId, anotherOrder.TransactionId);
        Assert.Equal(retry.TransactionId, (await gateway.ChargeAsync("order-1", 100m)).TransactionId);
    }

    [Fact]
    public async Task Refund_RefundsAChargeThatTimedOut_Once()
    {
        var gateway = Gateway(0.2); // charges, then times out
        await Assert.ThrowsAsync<PaymentGatewayUnavailableException>(() => gateway.ChargeAsync("order-1", 100m));

        Assert.True(await gateway.RefundAsync("order-1"));
        Assert.False(await gateway.RefundAsync("order-1")); // retried compensation
    }

    [Fact]
    public async Task Refund_WithNoCharge_DoesNothing()
    {
        var gateway = Gateway(0.1); // fails before charging
        await Assert.ThrowsAsync<PaymentGatewayUnavailableException>(() => gateway.ChargeAsync("order-1", 100m));

        Assert.False(await gateway.RefundAsync("order-1"));
        Assert.False(await gateway.RefundAsync("never-charged"));
    }
}
