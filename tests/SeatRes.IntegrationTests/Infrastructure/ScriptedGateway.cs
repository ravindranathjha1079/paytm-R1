using System.Collections.Concurrent;
using SeatRes.Api.Payments;

namespace SeatRes.IntegrationTests.Infrastructure;

public enum Script { Succeed, Decline, UnknownButCharged, UnknownNotCharged, Block }

/// <summary>Deterministic stand-in for the payment provider. Idempotent per gateway key, like a real provider.</summary>
public sealed class ScriptedGateway : IPaymentGateway
{
    private readonly ConcurrentQueue<Script> _plan = new();
    private TaskCompletionSource<GatewayStatus> _release = NewTcs<GatewayStatus>();
    private TaskCompletionSource _entered = NewTcs();

    public ConcurrentDictionary<string, (GatewayStatus Status, bool Refunded)> Book { get; } = new();
    public int ChargeCalls;
    public int RefundCalls;
    public volatile bool FailRefunds;

    public void Reset()
    {
        _plan.Clear();
        _release = NewTcs<GatewayStatus>();
        _entered = NewTcs();
        FailRefunds = false;
        ChargeCalls = 0;
        RefundCalls = 0;
        Book.Clear();
    }

    public void Plan(params Script[] steps)
    {
        foreach (var s in steps) _plan.Enqueue(s);
    }

    /// <summary>Completes when a Block-scripted charge is waiting inside the gateway.</summary>
    public Task Entered => _entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

    public void Release(GatewayStatus outcome) => _release.TrySetResult(outcome);

    public int Succeeded => Book.Values.Count(v => v.Status == GatewayStatus.Succeeded);
    public int Refunded => Book.Values.Count(v => v.Refunded);

    public async Task<GatewayStatus> ChargeAsync(string key, long amountPaise, string? hint, CancellationToken ct)
    {
        Interlocked.Increment(ref ChargeCalls);
        if (Book.TryGetValue(key, out var existing)) return existing.Status;
        var step = _plan.TryDequeue(out var s) ? s : Script.Succeed;
        switch (step)
        {
            case Script.Decline:
                Book[key] = (GatewayStatus.Declined, false);
                return GatewayStatus.Declined;
            case Script.UnknownButCharged:
                Book[key] = (GatewayStatus.Succeeded, false);
                return GatewayStatus.Unknown;
            case Script.UnknownNotCharged:
                return GatewayStatus.Unknown;
            case Script.Block:
                _entered.TrySetResult();
                var outcome = await _release.Task;
                if (outcome != GatewayStatus.Unknown) Book[key] = (outcome, false);
                return outcome;
            default:
                Book[key] = (GatewayStatus.Succeeded, false);
                return GatewayStatus.Succeeded;
        }
    }

    public Task<GatewayStatus> QueryAsync(string key, CancellationToken ct) =>
        Task.FromResult(Book.TryGetValue(key, out var e) ? e.Status : GatewayStatus.NotFound);

    public Task<bool> RefundAsync(string key, CancellationToken ct)
    {
        if (FailRefunds) return Task.FromResult(false);
        Interlocked.Increment(ref RefundCalls);
        if (Book.TryGetValue(key, out var e)) Book[key] = (e.Status, true);
        return Task.FromResult(true);
    }

    private static TaskCompletionSource<T> NewTcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
