using Microsoft.Win32;

namespace CodexUsageTray;

internal sealed class StartupRegistration
{
    private const string DefaultValueName = "CodexUsageTray";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string valueName;

    /// <summary>
    /// Creates a per-user startup registration using the application value name.
    /// </summary>
    /// <param name="valueName">An optional isolated registry value name.</param>
    internal StartupRegistration(string valueName = DefaultValueName)
    {
        this.valueName = valueName;
    }

    /// <summary>
    /// Reports whether the current executable is registered to start for this Windows user.
    /// </summary>
    /// <returns>True when the current executable is registered.</returns>
    internal bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return string.Equals(
            key?.GetValue(valueName) as string,
            GetCommand(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Enables or disables start-with-Windows for the current user.
    /// </summary>
    /// <param name="enabled">Whether startup should be enabled.</param>
    internal void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
        {
            key.SetValue(valueName, GetCommand(), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// Builds the quoted command stored in the per-user Run key.
    /// </summary>
    /// <returns>The current executable command.</returns>
    private static string GetCommand() => $"\"{Application.ExecutablePath}\"";
}
