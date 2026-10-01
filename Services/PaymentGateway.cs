using System.Collections.Concurrent;

namespace OrderProcessing.Services;

public interface IPaymentGateway
{
    /// <summary>
    /// Charges <paramref name="amount"/>. Calls with the same <paramref name="idempotencyKey"/> return the first result
    /// instead of charging again, so the caller can safely retry.
    /// </summary>
    /// <returns>Approved or declined. A decline is a business outcome: retrying will not change it.</returns>
    /// <exception cref="PaymentGatewayUnavailableException">Transient failure: safe to retry.</exception>
    Task<PaymentOutcome> ChargeAsync(string idempotencyKey, decimal amount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refunds the approved charge made with <paramref name="idempotencyKey"/>, if there is one.
    /// Returns false when nothing was charged. Calling it again is a no-op, so it is safe to retry.
    /// </summary>
    Task<bool> RefundAsync(string idempotencyKey, CancellationToken cancellationToken = default);
}

public record PaymentOutcome(bool Approved, string? TransactionId, string Message);

public class PaymentGatewayUnavailableException(string message) : Exception(message);

public class SimulatedPaymentGatewayOptions
{
    /// <summary>Share of calls that fail transiently (0-1). Half fail before charging, half after (a "timeout").</summary>
    public double TransientFailureRate { get; set; } = 0.3;

    /// <summary>Charges above this amount are declined.</summary>
    public decimal DeclineAbove { get; set; } = 5_000m;
}

/// <summary>
/// Stands in for a real payment provider. Keeps charges in memory, so it only works for a single local instance.
/// </summary>
public class SimulatedPaymentGateway(SimulatedPaymentGatewayOptions options, Random? random = null) : IPaymentGateway
{
    private readonly Random _random = random ?? Random.Shared;
    private readonly ConcurrentDictionary<string, PaymentOutcome> _charges = new();

    public async Task<PaymentOutcome> ChargeAsync(string idempotencyKey, decimal amount, CancellationToken cancellationToken = default)
    {
        await Task.Delay(500, cancellationToken); // simulate the provider's latency

        if (_charges.TryGetValue(idempotencyKey, out var previous))
            return previous; // a retry after a timeout: the customer was already charged once

        var roll = _random.NextDouble();
        if (roll < options.TransientFailureRate / 2)
            throw new PaymentGatewayUnavailableException("Payment provider unavailable (HTTP 503)");

        var outcome = amount > options.DeclineAbove
            ? new PaymentOutcome(false, null, $"Payment of {amount:0.00} was declined: over the {options.DeclineAbove:0.00} limit")
            : new PaymentOutcome(true, $"txn_{Guid.NewGuid():N}", $"Payment of {amount:0.00} approved");
        _charges[idempotencyKey] = outcome;

        if (roll < options.TransientFailureRate)
            throw new PaymentGatewayUnavailableException("Payment provider timed out after charging");

        return outcome;
    }

    public async Task<bool> RefundAsync(string idempotencyKey, CancellationToken cancellationToken = default)
    {
        await Task.Delay(200, cancellationToken);

        if (!_charges.TryGetValue(idempotencyKey, out var charge) || !charge.Approved)
            return false;

        _charges[idempotencyKey] = charge with { Approved = false, Message = $"Refunded {charge.TransactionId}" };
        return true;
    }
}
