using System.Runtime.InteropServices;
using Avalonia;

namespace Guardian.App;

internal static partial class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        CrashLog.InstallGlobalHandlers();
        try
        {
            if (args.Contains("--report"))
            {
                // The exe is a windowed app, so on Windows it has no console of its own; borrow the one it was started from.
                if (OperatingSystem.IsWindows() && !Console.IsOutputRedirected) AttachConsole(AttachParentProcess);
                var dataDir = args.SkipWhile(a => a != "--data-dir").Skip(1).FirstOrDefault();
                return CliReport.RunAsync(args.Contains("--json"), dataDir).GetAwaiter().GetResult();
            }
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            var path = CrashLog.Write("Startup failed", ex);
            Console.Error.WriteLine(ex);
            CrashLog.ShowNativeError(path);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<GuardianApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private const int AttachParentProcess = -1;

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);
}
