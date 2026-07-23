using System.Runtime.InteropServices;

namespace CodexUsageTray;

internal static class TrayIconRenderer
{
    private const int IconSize = 32;

    /// <summary>
    /// Renders a numeric tray icon for one rate-limit window.
    /// </summary>
    /// <param name="reading">The normalized limit reading.</param>
    /// <param name="left">The weekly allowance remaining above the current pacing target.</param>
    /// <returns>An independently owned Windows icon.</returns>
    internal static Icon Render(LimitReading reading, double? left)
    {
        string text = reading.State == LimitState.Available && reading.RemainingPercent is int remaining
            ? Math.Clamp(remaining, 0, 100).ToString()
            : "--";
        Color textColor = ResolveTextColor(left);

        using Bitmap bitmap = new(IconSize, IconSize);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);

        float fontSize = text.Length == 3 ? 14 : 22;
        using Font font = new("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        TextFormatFlags flags =
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(
            graphics,
            text,
            font,
            new Rectangle(1, 1, IconSize, IconSize),
            Color.Black,
            flags);
        TextRenderer.DrawText(
            graphics,
            text,
            font,
            new Rectangle(0, 0, IconSize, IconSize),
            textColor,
            flags);

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using Icon temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>
    /// Selects the tray text colour from the weekly pacing balance.
    /// </summary>
    /// <param name="left">The percentage remaining above the pacing target.</param>
    /// <returns>Red below target, green on target, or gray when unavailable.</returns>
    internal static Color ResolveTextColor(double? left) =>
        left is null
            ? Color.Gray
            : left < 0
                ? Color.Red
                : Color.LimeGreen;

    /// <summary>
    /// Releases a native icon handle created from a bitmap.
    /// </summary>
    /// <param name="handle">The native icon handle.</param>
    /// <returns><see langword="true"/> when the handle was released.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
