namespace CodexUsageTray;

internal static class SelfTests
{
    /// <summary>
    /// Runs deterministic package-free checks followed by one live Codex CLI smoke read.
    /// </summary>
    /// <returns>Zero when every check succeeds; otherwise one.</returns>
    internal static async Task<int> RunAsync()
    {
        try
        {
            DateTimeOffset now = DateTimeOffset.Now;
            long reset = now.AddHours(1).ToUnixTimeSeconds();

            UsageSnapshot both = Parse(
                Window(25, CodexRateLimitReader.FiveHourMinutes, reset),
                Window(60, CodexRateLimitReader.WeeklyMinutes, reset),
                now);
            Check(both.FiveHour.RemainingPercent == 75, "Five-hour remaining percentage was incorrect.");
            Check(both.Weekly.RemainingPercent == 40, "Weekly remaining percentage was incorrect.");

            UsageSnapshot single = Parse(
                Window(10, CodexRateLimitReader.FiveHourMinutes, reset),
                null,
                now);
            Check(single.FiveHour.State == LimitState.Available, "Single five-hour window was not available.");
            Check(single.Weekly.State == LimitState.Unavailable, "Missing weekly window was not unavailable.");

            UsageSnapshot weeklyOnly = Parse(
                Window(15, CodexRateLimitReader.WeeklyMinutes, reset),
                null,
                now);
            Check(weeklyOnly.FiveHour.State == LimitState.Unavailable, "Missing five-hour window was not unavailable.");
            Check(weeklyOnly.Weekly.State == LimitState.Available, "Single weekly window was not available.");

            UsageSnapshot reversed = Parse(
                Window(30, CodexRateLimitReader.WeeklyMinutes, reset),
                Window(40, CodexRateLimitReader.FiveHourMinutes, reset),
                now);
            Check(reversed.FiveHour.RemainingPercent == 60, "Reversed five-hour window was misclassified.");
            Check(reversed.Weekly.RemainingPercent == 70, "Reversed weekly window was misclassified.");

            UsageSnapshot clamped = Parse(
                Window(-10, CodexRateLimitReader.FiveHourMinutes, reset),
                Window(150, CodexRateLimitReader.WeeklyMinutes, reset),
                now);
            Check(clamped.FiveHour.RemainingPercent == 100, "Negative usage was not clamped.");
            Check(clamped.Weekly.RemainingPercent == 0, "Excess usage was not clamped.");

            ExpectThrows<CodexUsageException>(
                () => CodexRateLimitReader.ParseUsageResponse("{", now),
                "Malformed JSON was accepted.");
            ExpectThrows<CodexUsageException>(
                () => CodexRateLimitReader.ThrowIfProtocolError(
                    """{"id":2,"error":{"message":"denied"}}"""),
                "Protocol error was accepted.");

            TestWeeklyDailyRates(now);
            TestWeeklyEndOfDayTarget(now);
            TestWeeklyLeft(now);
            TestPopupReopen();

            using (CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(20)))
            {
                await ExpectThrowsAsync<OperationCanceledException>(
                    () => CodexRateLimitReader.WaitForResponseAsync(
                        new BlockingTextReader(),
                        2,
                        timeout.Token),
                    "Cancellation did not stop a pending protocol read.").ConfigureAwait(false);
            }

            TestAlertPersistence(now);
            TestStartupRegistration();
            TestDuplicateMutex();

            UsageSnapshot live = await new CodexRateLimitReader().FetchAsync().ConfigureAwait(false);
            Check(
                live.FiveHour.State == LimitState.Available || live.Weekly.State == LimitState.Available,
                "The live Codex smoke read returned no available usage window.");

            Console.WriteLine("Self-tests passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Self-test failed: {exception.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Verifies weekly per-day rates and unavailable reset handling.
    /// </summary>
    /// <param name="now">The timestamp used for deterministic rate calculations.</param>
    private static void TestWeeklyDailyRates(DateTimeOffset now)
    {
        LimitReading reading = new(LimitState.Available, 55, now.AddDays(4));
        Check(
            UsagePopup.FormatWeeklyDailyRates(reading, now)
                == "Per day: 15.0% used · 13.8% left",
            "Weekly per-day rates were incorrect.");
        Check(
            UsagePopup.FormatWeeklyDailyRates(reading with { ResetsAt = null }, now)
                == "Per-day rates unavailable",
            "Missing weekly reset time did not make per-day rates unavailable.");
    }

    /// <summary>
    /// Verifies the allowance target at the next whole-day weekly boundary.
    /// </summary>
    /// <param name="now">The timestamp used for deterministic target calculations.</param>
    private static void TestWeeklyEndOfDayTarget(DateTimeOffset now)
    {
        LimitReading reading = new(LimitState.Available, 55, now.AddDays(5).AddHours(11));
        Check(
            UsagePopup.FormatWeeklyEndOfDayTarget(reading, now)
                == "End of day: 71.4% left in 11h",
            "Weekly end-of-day target was incorrect.");
        Check(
            UsagePopup.FormatWeeklyEndOfDayTarget(reading with { ResetsAt = now.AddDays(5) }, now)
                == "End of day: 71.4% left now",
            "Whole-day weekly target was not due now.");
        Check(
            UsagePopup.FormatWeeklyEndOfDayTarget(reading with { ResetsAt = null }, now)
                == "End of day target unavailable",
            "Missing weekly reset time did not make the end-of-day target unavailable.");
    }

    /// <summary>
    /// Verifies the weekly allowance remaining above the next whole-day target.
    /// </summary>
    /// <param name="now">The timestamp used for deterministic target calculations.</param>
    private static void TestWeeklyLeft(DateTimeOffset now)
    {
        LimitReading reading = new(LimitState.Available, 55, now.AddDays(5).AddHours(11));
        double? left = UsagePopup.CalculateWeeklyLeft(reading, now);
        Check(left is < 0, "Weekly allowance left was not negative.");
        Check(
            UsagePopup.FormatWeeklyLeft(reading, now) == "Left: -16.4%",
            "Weekly allowance left was incorrect.");
        Check(
            TrayIconRenderer.ResolveTextColor(left) == Color.Red,
            "Negative weekly allowance left did not produce red tray text.");
        Check(
            TrayIconRenderer.ResolveTextColor(0) == Color.LimeGreen,
            "Non-negative weekly allowance left did not produce green tray text.");
        Check(
            TrayIconRenderer.ResolveTextColor(null) == Color.Gray,
            "Unavailable weekly allowance left did not produce gray tray text.");
        Check(
            UsagePopup.FormatWeeklyLeft(reading with { RemainingPercent = null }, now)
                == "Left unavailable",
            "Missing weekly percentage did not make allowance left unavailable.");
    }

    /// <summary>
    /// Verifies that a user close hides the reusable popup instead of disposing it.
    /// </summary>
    private static void TestPopupReopen()
    {
        using UsagePopup popup = new();
        popup.Show();
        popup.Close();
        Check(!popup.IsDisposed, "Closing the popup disposed it.");
        popup.Show();
        popup.Hide();
    }

    /// <summary>
    /// Builds and parses a general Codex response containing up to two windows.
    /// </summary>
    /// <param name="primary">The primary window JSON, or null.</param>
    /// <param name="secondary">The secondary window JSON, or null.</param>
    /// <param name="now">The refresh timestamp.</param>
    /// <returns>The normalized usage snapshot.</returns>
    private static UsageSnapshot Parse(string? primary, string? secondary, DateTimeOffset now)
    {
        List<string> properties = [];
        if (primary is not null)
        {
            properties.Add($"\"primary\":{primary}");
        }

        if (secondary is not null)
        {
            properties.Add($"\"secondary\":{secondary}");
        }

        string response =
            "{\"id\":2,\"result\":{\"rateLimitsByLimitId\":{\"codex\":{"
            + string.Join(',', properties)
            + "}}}}";
        return CodexRateLimitReader.ParseUsageResponse(response, now);
    }

    /// <summary>
    /// Builds one rate-limit window JSON object.
    /// </summary>
    /// <param name="usedPercent">The reported used percentage.</param>
    /// <param name="durationMinutes">The window duration in minutes.</param>
    /// <param name="reset">The Unix reset timestamp.</param>
    /// <returns>The serialized window.</returns>
    private static string Window(int usedPercent, long durationMinutes, long reset) =>
        $"{{\"usedPercent\":{usedPercent},\"windowDurationMins\":{durationMinutes},\"resetsAt\":{reset}}}";

    /// <summary>
    /// Verifies alert thresholds and deduplication across store instances.
    /// </summary>
    /// <param name="now">The timestamp used to create reset cycles.</param>
    private static void TestAlertPersistence(DateTimeOffset now)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"CodexUsageTray-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "alerts.json");
        try
        {
            LimitReading low = new(LimitState.Available, 20, now.AddHours(1));
            Check(new AlertStateStore(path).ShouldAlert("five-hour", low), "First low alert was suppressed.");
            Check(!new AlertStateStore(path).ShouldAlert("five-hour", low), "Alert was not persisted.");
            Check(
                new AlertStateStore(path).ShouldAlert(
                    "five-hour",
                    low with { ResetsAt = now.AddHours(2) }),
                "A new reset cycle was suppressed.");
            Check(
                !new AlertStateStore(path).ShouldAlert(
                    "weekly",
                    new LimitReading(LimitState.Available, 21, now.AddDays(1))),
                "An alert above the threshold was emitted.");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Verifies reversible per-user startup registration with an isolated registry value.
    /// </summary>
    private static void TestStartupRegistration()
    {
        string valueName = $"CodexUsageTray.SelfTest.{Guid.NewGuid():N}";
        StartupRegistration registration = new(valueName);
        try
        {
            registration.SetEnabled(false);
            Check(!registration.IsEnabled(), "Startup registration began enabled.");
            registration.SetEnabled(true);
            Check(registration.IsEnabled(), "Startup registration was not enabled.");
            registration.SetEnabled(false);
            Check(!registration.IsEnabled(), "Startup registration was not disabled.");
        }
        finally
        {
            registration.SetEnabled(false);
        }
    }

    /// <summary>
    /// Verifies that a second named mutex detects an existing application instance.
    /// </summary>
    private static void TestDuplicateMutex()
    {
        string name = $"Local\\CodexUsageTray.SelfTest.{Guid.NewGuid():N}";
        using Mutex first = new(true, name, out bool firstCreated);
        using Mutex second = new(true, name, out bool secondCreated);
        Check(firstCreated && !secondCreated, "The duplicate-instance mutex was not detected.");
        first.ReleaseMutex();
    }

    /// <summary>
    /// Throws when a self-test condition is false.
    /// </summary>
    /// <param name="condition">The condition under test.</param>
    /// <param name="message">The failure message.</param>
    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>
    /// Verifies that an action throws the requested exception type.
    /// </summary>
    /// <typeparam name="TException">The expected exception type.</typeparam>
    /// <param name="action">The action under test.</param>
    /// <param name="message">The failure message.</param>
    private static void ExpectThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    /// <summary>
    /// Verifies that an asynchronous action throws the requested exception type.
    /// </summary>
    /// <typeparam name="TException">The expected exception type.</typeparam>
    /// <param name="action">The asynchronous action under test.</param>
    /// <param name="message">The failure message.</param>
    private static async Task ExpectThrowsAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private sealed class BlockingTextReader : TextReader
    {
        /// <summary>
        /// Waits indefinitely until the supplied cancellation token is canceled.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token under test.</param>
        /// <returns>No line; this operation completes only through cancellation.</returns>
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return null;
        }
    }
}
