namespace TC.Tools.Unlock;

/// <summary>
/// Pure decision rules for the unlock path (§3.3.1 / §5.4 / §5.5), extracted from
/// the GATT host so `selftest` exercises the SAME predicates the server uses
/// instead of a parallel re-implementation. These rules were previously only
/// reachable through a live GATT connection; verify flagged that as the weak
/// spot (a regression introduced by a future fix would have been invisible
/// without a phone).
/// </summary>
public static class UnlockPolicy
{
    /// <summary>§5.5 minimum spacing between two unlocks in one session.</summary>
    public const int UnlockThrottleMs = 1500;

    /// <summary>§3.3.1 consecutive PROOF failures that invalidate the PSK.</summary>
    public const int MaxProofFailures = 5;

    /// <summary>
    /// Sliding-window throttle: true means "reject as throttled". The caller
    /// advances its own window base on every attempt, so sustained high-frequency
    /// retries stay throttled instead of slipping through on the first-attempt
    /// anniversary (stricter than the raw 1500 ms wording; see README §6.1).
    /// </summary>
    public static bool ShouldThrottle(long nowTicks, long lastUnlockTicks, int windowMs = UnlockThrottleMs)
        => lastUnlockTicks != 0 && nowTicks - lastUnlockTicks < windowMs;

    public readonly record struct ProofFailureOutcome(int FailCount, bool InvalidatePsk);

    /// <summary>
    /// Folds one failed PROOF into the running count. The 5th consecutive failure
    /// invalidates the PSK and resets the counter.
    /// </summary>
    public static ProofFailureOutcome RegisterProofFailure(int currentFailCount, int maxFailures = MaxProofFailures)
        => currentFailCount + 1 >= maxFailures
            ? new ProofFailureOutcome(0, true)
            : new ProofFailureOutcome(currentFailCount + 1, false);

    /// <summary>
    /// §5.4: what must happen before injecting. Returns null when the password may
    /// be typed, otherwise the `reason` to report.
    /// </summary>
    public static string? RejectionReason(bool hasPassword, bool locked, bool injectWhenUnlocked)
    {
        if (!hasPassword) return Reasons.NoPassword;
        if (!locked && !injectWhenUnlocked) return Reasons.NotLocked;
        return null;
    }
}
