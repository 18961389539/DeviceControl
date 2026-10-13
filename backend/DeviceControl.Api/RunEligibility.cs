namespace DeviceControl.Api;

/// <summary>
/// F04: unified station run-eligibility gate. Cycle starts, Auto mode and
/// configuration changes consult registered per-station blockers (unresolved
/// deployments, persistent recovery state) instead of each feature checking in
/// isolation. Fail closed: an unreadable or missing check blocks the station.
/// </summary>
public sealed class RunEligibilityRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Func<string?, string?>> _checks = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string stationId, Func<string?> unresolvedReason)
    {
        lock (_gate) _checks[stationId] = _ => unresolvedReason();
    }
    public void Register(string stationId, Func<string?, string?> unresolvedReason, bool supportsScopedRecovery)
    { lock (_gate) _checks[stationId] = unresolvedReason; }
    public void Unregister(string stationId) { lock (_gate) _checks.Remove(stationId); }

    /// <summary>Returns a human-readable blocking reason, or null when the station may run.</summary>
    public string? BlockingReason(string stationId)
        => BlockingReason(stationId, null);
    public string? BlockingReason(string stationId, string? recoveryDeploymentId)
    {
        lock (_gate)
        {
            if (!_checks.TryGetValue(stationId, out var check)) return "运行资格检查尚未就绪，已阻止运行";
            try { return check(recoveryDeploymentId); }
            catch (Exception ex)
            {
                // Fail closed: a broken eligibility check must not silently admit a cycle.
                return "运行资格检查失败（" + ex.Message + "），已阻止运行";
            }
        }
    }
}
