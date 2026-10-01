using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using OrderProcessing.Models;

namespace OrderProcessing.Tests;

/// <summary>
/// A scripted TaskOrchestrationContext: each activity name maps to a result or a failure, and every call is recorded
/// together with the TaskOptions (retry policy) it was made with.
/// </summary>
public class FakeOrchestrationContext
{
    private readonly Dictionary<string, Func<Order, OrderResult>> _activities = new();
    private readonly Mock<TaskOrchestrationContext> _mock = new() { CallBase = true };

    public List<(string Name, TaskOptions? Options)> Calls { get; } = [];
    public List<object?> CustomStatuses { get; } = [];
    public TaskOrchestrationContext Object => _mock.Object;

    public FakeOrchestrationContext(Order input)
    {
        _mock.Setup(c => c.GetInput<Order>()).Returns(input);
        _mock.Setup(c => c.SetCustomStatus(It.IsAny<object?>())).Callback<object?>(CustomStatuses.Add);
        _mock.Protected().SetupGet<ILoggerFactory>("LoggerFactory").Returns(NullLoggerFactory.Instance);

        // Moq matches generic setups by assignability and the last setup wins, so the <object> setup (used by the
        // non-generic CallActivityAsync) must come before the more specific <OrderResult> one.
        _mock.Setup(c => c.CallActivityAsync<object>(It.IsAny<TaskName>(), It.IsAny<object?>(), It.IsAny<TaskOptions?>()))
            .Returns<TaskName, object?, TaskOptions?>(async (name, payload, options) => await Invoke(name, payload, options));
        _mock.Setup(c => c.CallActivityAsync<OrderResult>(It.IsAny<TaskName>(), It.IsAny<object?>(), It.IsAny<TaskOptions?>()))
            .Returns<TaskName, object?, TaskOptions?>((name, payload, options) => Invoke(name, payload, options));
    }

    public FakeOrchestrationContext Returns(string activity, OrderResult result) => Returns(activity, _ => result);

    public FakeOrchestrationContext Returns(string activity, Func<Order, OrderResult> result)
    {
        _activities[activity] = result;
        return this;
    }

    /// <summary>Simulates an activity that still fails after the retry policy is exhausted.</summary>
    public FakeOrchestrationContext FailsAfterRetries(string activity, string error) =>
        Returns(activity, _ => throw new TaskFailedException(activity, 0, new TaskFailureDetails("System.Exception", error, null, null, null)));

    public IEnumerable<string> CalledActivities => Calls.Select(c => c.Name);

    public TaskOptions? OptionsFor(string activity) => Calls.Single(c => c.Name == activity).Options;

    private Task<OrderResult> Invoke(TaskName name, object? payload, TaskOptions? options)
    {
        Calls.Add((name.Name, options));
        var result = _activities.TryGetValue(name.Name, out var activity)
            ? activity((Order)payload!)
            : OrderResult.Ok($"{name.Name} ok");
        return Task.FromResult(result);
    }
}
