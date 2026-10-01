using System.Collections.Concurrent;

namespace TemplateAdapter.Simulator;

/// <summary>An account as the simulated ExternalSystem stores it (same JSON shape the client sends).</summary>
public sealed record SimulatedAccount(string AccountKey, string Name, string? CountryCode, string? Email, string? ParentAccountKey);

/// <summary>
/// A planned failure: the next <see cref="Remaining"/> API calls (or every call until
/// <see cref="Until"/>) answer <see cref="Status"/> with the error <see cref="Code"/> and
/// <see cref="Message"/>.
/// </summary>
public sealed record FaultPlan(int Status, int? Remaining, DateTimeOffset? Until, int? RetryAfterSeconds, string Code, string Message);

/// <summary>
/// A fault to inject into the next API calls (body of <c>POST /emulator/faults</c>). Any 4xx or 5xx
/// status; <see cref="Code"/> and <see cref="Message"/> default to generic values, so set them to
/// the real system's error code to rehearse a specific failure.
/// </summary>
public sealed record FaultRequest(int Status, int? Count, int? DurationSeconds, int? RetryAfterSeconds, string? Code = null, string? Message = null);

/// <summary>Marker type for <c>WebApplicationFactory</c> in tests (avoids a clash with the adapter's <c>Program</c>).</summary>
public sealed class SimulatorEntryPoint;

/// <summary>The simulator's in-memory data and fault plan. Restarting the simulator resets it.</summary>
public sealed class SimulatorState(TimeProvider clock)
{
    private readonly Lock _faultLock = new();
    private FaultPlan? _fault;

    /// <summary>Accounts keyed on their external key (the upstream id).</summary>
    public ConcurrentDictionary<string, SimulatedAccount> Accounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Accounts ExternalSystem owns: upserts answer 409.</summary>
    public ConcurrentDictionary<string, bool> Owned { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Plans a failure for the next API calls.</summary>
    public void PlanFault(FaultPlan plan)
    {
        lock (_faultLock)
        {
            _fault = plan;
        }
    }

    /// <summary>Clears any planned failure.</summary>
    public void ClearFault()
    {
        lock (_faultLock)
        {
            _fault = null;
        }
    }

    /// <summary>Returns the fault to apply to this call, if any, and counts it down.</summary>
    public FaultPlan? TakeFault()
    {
        lock (_faultLock)
        {
            if (_fault is null)
                return null;

            if (_fault.Until is { } until && clock.GetUtcNow() >= until)
            {
                _fault = null;
                return null;
            }

            var current = _fault;
            if (current.Remaining is { } remaining)
                _fault = remaining <= 1 ? null : current with { Remaining = remaining - 1 };

            return current;
        }
    }
}
