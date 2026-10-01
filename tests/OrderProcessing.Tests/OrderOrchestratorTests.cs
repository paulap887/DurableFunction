using OrderProcessing.Functions;
using OrderProcessing.Models;

namespace OrderProcessing.Tests;

public class OrderOrchestratorTests
{
    private const string Validate = nameof(OrderActivities.ValidateOrder);
    private const string Reserve = nameof(OrderActivities.ReserveInventory);
    private const string Release = nameof(OrderActivities.ReleaseInventory);
    private const string Refund = nameof(OrderActivities.RefundPayment);
    private const string Pay = nameof(OrderActivities.ProcessPayment);
    private const string Email = nameof(OrderActivities.SendConfirmationEmail);

    private static Order NewOrder() => TestData.ValidOrder();

    private static Task<OrderResult> Run(FakeOrchestrationContext context) =>
        new OrderOrchestrator().RunOrderOrchestration(context.Object);

    [Fact]
    public async Task HappyPath_RunsEveryStepInOrder_AndCompletes()
    {
        var context = new FakeOrchestrationContext(NewOrder());

        var result = await Run(context);

        Assert.True(result.Success);
        Assert.Equal(OrderStatus.Completed, result.Order!.Status);
        Assert.Equal([Validate, Reserve, Pay, Email], context.CalledActivities);
    }

    [Fact]
    public async Task InvalidOrder_StopsBeforeReservingInventory()
    {
        var context = new FakeOrchestrationContext(NewOrder())
            .Returns(Validate, OrderResult.Fail("Customer name is required"));

        var result = await Run(context);

        Assert.False(result.Success);
        Assert.Equal(OrderStatus.Failed, result.Order!.Status);
        Assert.Equal([Validate], context.CalledActivities);
    }

    [Fact]
    public async Task OutOfStock_StopsBeforePayment_WithNothingToCompensate()
    {
        var context = new FakeOrchestrationContext(NewOrder())
            .Returns(Reserve, OrderResult.Fail("Not enough stock"));

        var result = await Run(context);

        Assert.False(result.Success);
        Assert.Equal([Validate, Reserve], context.CalledActivities);
    }

    [Fact]
    public async Task PaymentDeclined_ReleasesInventory_AndCancelsOrder()
    {
        var context = new FakeOrchestrationContext(NewOrder())
            .Returns(Pay, OrderResult.Fail("Card declined"));

        var result = await Run(context);

        Assert.False(result.Success);
        Assert.Equal(OrderStatus.Cancelled, result.Order!.Status);
        Assert.Equal("Card declined", result.Message);
        Assert.Equal([Validate, Reserve, Pay, Release], context.CalledActivities); // declined: nothing to refund
    }

    [Fact]
    public async Task PaymentFailingAfterAllRetries_RefundsPossibleCharge_ReleasesInventory_AndCancelsOrder()
    {
        var context = new FakeOrchestrationContext(NewOrder())
            .FailsAfterRetries(Pay, "Payment provider unavailable (HTTP 503)");

        var result = await Run(context);

        Assert.False(result.Success);
        Assert.Equal(OrderStatus.Cancelled, result.Order!.Status);
        Assert.Contains("HTTP 503", result.Message);
        Assert.Equal([Validate, Reserve, Pay, Refund, Release], context.CalledActivities);
    }

    [Fact]
    public async Task EmailFailingAfterAllRetries_StillCompletesOrder_WithoutCompensation()
    {
        var context = new FakeOrchestrationContext(NewOrder())
            .FailsAfterRetries(Email, "SMTP timeout");

        var result = await Run(context);

        Assert.True(result.Success);
        Assert.Equal(OrderStatus.Completed, result.Order!.Status);
        Assert.Equal("Order completed but email notification failed", result.Message);
        Assert.DoesNotContain(Release, context.CalledActivities);
        Assert.DoesNotContain(Refund, context.CalledActivities);
    }

    [Fact]
    public async Task RetryPolicies_ApplyToPaymentEmailAndCompensation_ButNotToBusinessChecks()
    {
        var context = new FakeOrchestrationContext(NewOrder())
            .Returns(Pay, OrderResult.Fail("Card declined"));

        await Run(context);

        Assert.Null(context.OptionsFor(Validate));
        Assert.Null(context.OptionsFor(Reserve));
        Assert.Same(OrderOrchestrator.PaymentRetry, context.OptionsFor(Pay));
        Assert.Same(OrderOrchestrator.CompensationRetry, context.OptionsFor(Release));

        var happy = new FakeOrchestrationContext(NewOrder());
        await Run(happy);
        Assert.Same(OrderOrchestrator.EmailRetry, happy.OptionsFor(Email));

        var unavailable = new FakeOrchestrationContext(NewOrder()).FailsAfterRetries(Pay, "HTTP 503");
        await Run(unavailable);
        Assert.Same(OrderOrchestrator.CompensationRetry, unavailable.OptionsFor(Refund));
    }

    [Fact]
    public void PaymentRetryPolicy_BacksOffExponentially()
    {
        var policy = OrderOrchestrator.PaymentRetry.Retry!.Policy!;

        Assert.Equal(4, policy.MaxNumberOfAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), policy.FirstRetryInterval);
        Assert.Equal(2.0, policy.BackoffCoefficient);
    }

    [Fact]
    public async Task ReportsProgressThroughCustomStatus()
    {
        var context = new FakeOrchestrationContext(NewOrder())
            .Returns(Pay, OrderResult.Fail("Card declined"));

        await Run(context);

        var steps = context.CustomStatuses.Select(s => s!.GetType().GetProperty("step")!.GetValue(s)).ToList();
        Assert.Equal(["Validating", "ReservingInventory", "ProcessingPayment", "Compensating"], steps);
    }
}
