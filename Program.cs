namespace CodexUsageTray;

internal static class Program
{
    private const string MutexName = "Local\\CodexUsageTray.SingleInstance";

    /// <summary>
    /// Starts the tray application or runs its package-free self-tests.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>The process exit code.</returns>
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            return SelfTests.RunAsync().GetAwaiter().GetResult();
        }

        using Mutex mutex = new(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
        GC.KeepAlive(mutex);
        return 0;
    }
}
