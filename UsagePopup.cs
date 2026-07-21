namespace CodexUsageTray;

internal sealed class UsagePopup : Form
{
    private readonly Label _fiveHourValue = new();
    private readonly ProgressBar _fiveHourProgress = new();
    private readonly Label _fiveHourReset = new();
    private readonly Label _weeklyValue = new();
    private readonly ProgressBar _weeklyProgress = new();
    private readonly Label _weeklyReset = new();
    private readonly Label _status = new();
    private readonly Label _error = new();
    private readonly System.Windows.Forms.Timer _countdownTimer = new() { Interval = 1_000 };
    private UsageSnapshot _snapshot = UsageSnapshot.Initial();
    private bool _refreshing;

    /// <summary>
    /// Creates the compact combined usage popup.
    /// </summary>
    internal UsagePopup()
    {
        Text = "Codex usage";
        ClientSize = new Size(360, 238);
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;

        Label title = new()
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Location = new Point(12, 12),
            Text = "Codex limits",
        };
        ConfigureValueLabel(_fiveHourValue, "5-hour", 45);
        ConfigureProgress(_fiveHourProgress, 65);
        ConfigureResetLabel(_fiveHourReset, 91);
        ConfigureValueLabel(_weeklyValue, "Weekly", 121);
        ConfigureProgress(_weeklyProgress, 141);
        ConfigureResetLabel(_weeklyReset, 167);

        _status.SetBounds(12, 195, 336, 18);
        _status.ForeColor = SystemColors.GrayText;
        _error.SetBounds(12, 214, 336, 20);
        _error.AutoEllipsis = true;
        _error.ForeColor = Color.Firebrick;

        Controls.AddRange([
            title,
            _fiveHourValue,
            _fiveHourProgress,
            _fiveHourReset,
            _weeklyValue,
            _weeklyProgress,
            _weeklyReset,
            _status,
            _error,
        ]);

        _countdownTimer.Tick += (_, _) => Render();
        _countdownTimer.Start();
        Deactivate += (_, _) => Hide();
        Render();
    }

    /// <summary>
    /// Updates the snapshot and refresh state shown by the popup.
    /// </summary>
    /// <param name="snapshot">The latest normalized usage snapshot.</param>
    /// <param name="refreshing">Whether a refresh is currently running.</param>
    internal void UpdateSnapshot(UsageSnapshot snapshot, bool refreshing)
    {
        _snapshot = snapshot;
        _refreshing = refreshing;
        Render();
    }

    /// <summary>
    /// Positions and shows the popup near the notification area selected by the cursor.
    /// </summary>
    internal void ShowNearTaskbar()
    {
        Rectangle workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(workingArea.Right - Width - 8, workingArea.Bottom - Height - 8);
        Show();
        Activate();
    }

    /// <summary>
    /// Hides instead of disposing the reusable popup when the user closes it.
    /// </summary>
    /// <param name="eventArgs">The form-closing event arguments.</param>
    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
        if (eventArgs.CloseReason == CloseReason.UserClosing)
        {
            eventArgs.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(eventArgs);
    }

    /// <summary>
    /// Releases the countdown timer owned by the popup.
    /// </summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _countdownTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Configures a heading/value label for one rate-limit window.
    /// </summary>
    /// <param name="label">The label to configure.</param>
    /// <param name="text">The window heading.</param>
    /// <param name="top">The top coordinate.</param>
    private static void ConfigureValueLabel(Label label, string text, int top)
    {
        label.SetBounds(12, top, 336, 18);
        label.Text = text;
    }

    /// <summary>
    /// Configures a progress bar for one rate-limit window.
    /// </summary>
    /// <param name="progress">The progress bar to configure.</param>
    /// <param name="top">The top coordinate.</param>
    private static void ConfigureProgress(ProgressBar progress, int top)
    {
        progress.SetBounds(12, top, 336, 18);
        progress.Maximum = 100;
    }

    /// <summary>
    /// Configures a reset-countdown label for one rate-limit window.
    /// </summary>
    /// <param name="label">The label to configure.</param>
    /// <param name="top">The top coordinate.</param>
    private static void ConfigureResetLabel(Label label, int top)
    {
        label.SetBounds(12, top, 336, 18);
        label.ForeColor = SystemColors.GrayText;
    }

    /// <summary>
    /// Renders the current snapshot and live reset countdowns.
    /// </summary>
    private void Render()
    {
        RenderLimit(_fiveHourValue, _fiveHourProgress, _fiveHourReset, "5-hour", _snapshot.FiveHour);
        RenderLimit(_weeklyValue, _weeklyProgress, _weeklyReset, "Weekly", _snapshot.Weekly);
        _status.Text = _refreshing
            ? "Refreshing…"
            : _snapshot.ErrorMessage is not null
                ? $"Refresh failed {_snapshot.RefreshedAt.ToLocalTime():HH:mm:ss}"
                : $"Updated {_snapshot.RefreshedAt.ToLocalTime():HH:mm:ss}";
        _error.Text = _snapshot.ErrorMessage ?? string.Empty;
        _error.Visible = !string.IsNullOrWhiteSpace(_snapshot.ErrorMessage);
    }

    /// <summary>
    /// Renders one normalized limit reading.
    /// </summary>
    /// <param name="valueLabel">The heading/value label.</param>
    /// <param name="progress">The remaining-percentage progress bar.</param>
    /// <param name="resetLabel">The reset-countdown label.</param>
    /// <param name="name">The display name.</param>
    /// <param name="reading">The normalized limit reading.</param>
    private static void RenderLimit(
        Label valueLabel,
        ProgressBar progress,
        Label resetLabel,
        string name,
        LimitReading reading)
    {
        bool available = reading.State == LimitState.Available && reading.RemainingPercent is int;
        int remaining = available ? Math.Clamp(reading.RemainingPercent!.Value, 0, 100) : 0;
        valueLabel.Text = available ? $"{name}: {remaining}% remaining" : $"{name}: N/A";
        progress.Value = remaining;
        resetLabel.Text = available ? FormatReset(reading.ResetsAt) : "Reset unavailable";
    }

    /// <summary>
    /// Formats a reset timestamp as a live countdown.
    /// </summary>
    /// <param name="resetsAt">The local reset timestamp, if supplied by Codex.</param>
    /// <returns>A short reset description.</returns>
    private static string FormatReset(DateTimeOffset? resetsAt)
    {
        if (resetsAt is null)
        {
            return "Reset unavailable";
        }

        TimeSpan remaining = resetsAt.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            return "Reset due now";
        }

        return remaining.TotalDays >= 1
            ? $"Resets in {(int)remaining.TotalDays}d {remaining.Hours}h"
            : $"Resets in {(int)remaining.TotalHours}h {remaining.Minutes}m";
    }
}
