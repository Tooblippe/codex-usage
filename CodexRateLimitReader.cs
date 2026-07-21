using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace CodexUsageTray;

internal sealed class CodexUsageException : Exception
{
    /// <summary>
    /// Creates an exception for an actionable Codex usage-read failure.
    /// </summary>
    /// <param name="message">The failure message.</param>
    internal CodexUsageException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates an exception for an actionable Codex usage-read failure with an inner error.
    /// </summary>
    /// <param name="message">The failure message.</param>
    /// <param name="innerException">The underlying error.</param>
    internal CodexUsageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class CodexRateLimitReader
{
    internal const long FiveHourMinutes = 300;
    internal const long WeeklyMinutes = 10_080;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Reads the current Codex rate-limit snapshot through the installed Codex CLI.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for application shutdown.</param>
    /// <returns>The normalized five-hour and weekly snapshot.</returns>
    internal async Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(RequestTimeout);

        try
        {
            return await FetchCoreAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CodexUsageException(
                "Codex usage request timed out. Check that the Codex CLI is installed and signed in.",
                exception);
        }
        catch (Win32Exception exception)
        {
            throw new CodexUsageException(
                "Could not start Codex. Ensure the Codex CLI is installed, on PATH, and signed in with 'codex login'.",
                exception);
        }
        catch (IOException exception)
        {
            throw new CodexUsageException(
                "Could not communicate with Codex. Restart the Codex CLI and try again.",
                exception);
        }
    }

    /// <summary>
    /// Runs one JSONL app-server exchange and always terminates the child process.
    /// </summary>
    /// <param name="cancellationToken">Timeout and shutdown cancellation token.</param>
    /// <returns>The normalized usage snapshot.</returns>
    private static async Task<UsageSnapshot> FetchCoreAsync(CancellationToken cancellationToken)
    {
        string commandProcessor = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        ProcessStartInfo startInfo = new(commandProcessor)
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/s");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("codex app-server --stdio");

        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new CodexUsageException("Could not start the Codex CLI.");
        }

        try
        {
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await WriteLineAsync(
                process.StandardInput,
                """
                {"id":1,"method":"initialize","params":{"clientInfo":{"name":"codex-usage-tray","version":"1.0.0"},"capabilities":{"experimentalApi":true}}}
                """,
                cancellationToken).ConfigureAwait(false);

            string initializeResponse = await WaitForResponseAsync(
                process.StandardOutput,
                1,
                cancellationToken).ConfigureAwait(false);
            ThrowIfProtocolError(initializeResponse);

            await WriteLineAsync(
                process.StandardInput,
                """{"method":"initialized"}""",
                cancellationToken).ConfigureAwait(false);
            await WriteLineAsync(
                process.StandardInput,
                """{"id":2,"method":"account/rateLimits/read","params":null}""",
                cancellationToken).ConfigureAwait(false);

            string usageResponse = await WaitForResponseAsync(
                process.StandardOutput,
                2,
                cancellationToken).ConfigureAwait(false);
            ThrowIfProtocolError(usageResponse);
            return ParseUsageResponse(usageResponse, DateTimeOffset.Now);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>
    /// Writes and flushes one JSONL protocol message.
    /// </summary>
    /// <param name="writer">The app-server standard-input writer.</param>
    /// <param name="message">The JSON message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static async Task WriteLineAsync(
        StreamWriter writer,
        string message,
        CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(message.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads JSONL messages until the requested response ID arrives, ignoring notifications.
    /// </summary>
    /// <param name="reader">The app-server standard-output reader.</param>
    /// <param name="responseId">The numeric response ID to await.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The complete matching JSON response line.</returns>
    internal static async Task<string> WaitForResponseAsync(
        TextReader reader,
        int responseId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                throw new CodexUsageException(
                    "Codex closed before returning usage. Ensure it is installed and signed in with 'codex login'.");
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("id", out JsonElement idElement)
                    && idElement.ValueKind == JsonValueKind.Number
                    && idElement.TryGetInt32(out int id)
                    && id == responseId)
                {
                    return line;
                }
            }
            catch (JsonException exception)
            {
                throw new CodexUsageException(
                    "Codex returned invalid protocol data. Update the Codex CLI and try again.",
                    exception);
            }
        }
    }

    /// <summary>
    /// Throws a readable exception when a JSON-RPC response contains an error.
    /// </summary>
    /// <param name="response">The JSON response line.</param>
    internal static void ThrowIfProtocolError(string response)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(response);
            if (!document.RootElement.TryGetProperty("error", out JsonElement error))
            {
                return;
            }

            string detail = error.TryGetProperty("message", out JsonElement message)
                ? message.GetString() ?? "Unknown Codex error"
                : error.ToString();
            throw new CodexUsageException(
                $"Codex could not return usage: {detail}. If needed, run 'codex login'.");
        }
        catch (JsonException exception)
        {
            throw new CodexUsageException(
                "Codex returned invalid protocol data. Update the Codex CLI and try again.",
                exception);
        }
    }

    /// <summary>
    /// Parses and normalizes a rate-limit response by its reported window durations.
    /// </summary>
    /// <param name="response">The complete account/rateLimits/read response.</param>
    /// <param name="refreshedAt">The local refresh timestamp.</param>
    /// <returns>The normalized snapshot.</returns>
    internal static UsageSnapshot ParseUsageResponse(string response, DateTimeOffset refreshedAt)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(response);
            JsonElement result = document.RootElement.GetProperty("result");
            JsonElement bucket = SelectCodexBucket(result);
            Dictionary<long, LimitReading> readings = [];

            AddWindow(bucket, "primary", readings);
            AddWindow(bucket, "secondary", readings);

            LimitReading fiveHour = FindWindow(readings, FiveHourMinutes);
            LimitReading weekly = FindWindow(readings, WeeklyMinutes);
            return new UsageSnapshot(fiveHour, weekly, refreshedAt, null);
        }
        catch (CodexUsageException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidOperationException
            or KeyNotFoundException
            or FormatException
            or OverflowException)
        {
            throw new CodexUsageException(
                "Codex returned an unsupported usage response. Update Codex Usage Tray or the Codex CLI.",
                exception);
        }
    }

    /// <summary>
    /// Selects the general Codex bucket, preferring the current multi-bucket response.
    /// </summary>
    /// <param name="result">The protocol result object.</param>
    /// <returns>The general Codex rate-limit bucket.</returns>
    private static JsonElement SelectCodexBucket(JsonElement result)
    {
        if (result.TryGetProperty("rateLimitsByLimitId", out JsonElement buckets)
            && buckets.ValueKind == JsonValueKind.Object
            && buckets.TryGetProperty("codex", out JsonElement codexBucket)
            && codexBucket.ValueKind == JsonValueKind.Object)
        {
            return codexBucket;
        }

        if (result.TryGetProperty("rateLimits", out JsonElement legacyBucket)
            && legacyBucket.ValueKind == JsonValueKind.Object)
        {
            return legacyBucket;
        }

        throw new CodexUsageException("Codex did not return a general usage bucket.");
    }

    /// <summary>
    /// Adds one available primary or secondary window to the normalized collection.
    /// </summary>
    /// <param name="bucket">The selected rate-limit bucket.</param>
    /// <param name="propertyName">The primary or secondary property name.</param>
    /// <param name="readings">The destination collection.</param>
    private static void AddWindow(
        JsonElement bucket,
        string propertyName,
        IDictionary<long, LimitReading> readings)
    {
        if (!bucket.TryGetProperty(propertyName, out JsonElement window)
            || window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("windowDurationMins", out JsonElement durationElement)
            || durationElement.ValueKind != JsonValueKind.Number
            || !durationElement.TryGetInt64(out long duration))
        {
            return;
        }

        int used = window.GetProperty("usedPercent").GetInt32();
        int remaining = (int)Math.Clamp(100L - used, 0L, 100L);
        DateTimeOffset? resetsAt = ReadResetTime(window);
        readings[duration] = new LimitReading(LimitState.Available, remaining, resetsAt);
    }

    /// <summary>
    /// Converts a valid Unix reset timestamp to local time.
    /// </summary>
    /// <param name="window">The rate-limit window object.</param>
    /// <returns>The local reset timestamp, or null when unavailable or invalid.</returns>
    private static DateTimeOffset? ReadResetTime(JsonElement window)
    {
        if (!window.TryGetProperty("resetsAt", out JsonElement resetElement)
            || resetElement.ValueKind != JsonValueKind.Number
            || !resetElement.TryGetInt64(out long resetSeconds))
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(resetSeconds).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Finds a window by duration or returns an unavailable reading.
    /// </summary>
    /// <param name="readings">Normalized readings keyed by duration.</param>
    /// <param name="durationMinutes">The expected duration in minutes.</param>
    /// <returns>The matching or unavailable reading.</returns>
    private static LimitReading FindWindow(
        IReadOnlyDictionary<long, LimitReading> readings,
        long durationMinutes) =>
        readings.TryGetValue(durationMinutes, out LimitReading? reading)
            ? reading
            : new LimitReading(LimitState.Unavailable, null, null);
}
