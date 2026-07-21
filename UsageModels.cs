namespace CodexUsageTray;

internal enum LimitState
{
    Available,
    Unavailable,
    Error,
}

internal sealed record LimitReading(
    LimitState State,
    int? RemainingPercent,
    DateTimeOffset? ResetsAt);

internal sealed record UsageSnapshot(
    LimitReading FiveHour,
    LimitReading Weekly,
    DateTimeOffset RefreshedAt,
    string? ErrorMessage)
{
    /// <summary>
    /// Creates a snapshot where both windows are unavailable because a refresh failed.
    /// </summary>
    /// <param name="message">The actionable failure message.</param>
    /// <returns>An error snapshot.</returns>
    internal static UsageSnapshot Error(string message) => new(
        new LimitReading(LimitState.Error, null, null),
        new LimitReading(LimitState.Error, null, null),
        DateTimeOffset.Now,
        message);

    /// <summary>
    /// Creates an initial snapshot before the first refresh completes.
    /// </summary>
    /// <returns>An unavailable snapshot.</returns>
    internal static UsageSnapshot Initial() => new(
        new LimitReading(LimitState.Unavailable, null, null),
        new LimitReading(LimitState.Unavailable, null, null),
        DateTimeOffset.Now,
        null);
}
