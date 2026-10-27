using System.Runtime.InteropServices;
using Avalonia.Threading;
using Guardian.Data;

namespace Guardian.App;

/// <summary>
/// Writes unexpected errors to crash.log in Guardian's own data folder, so a failed start
/// tells the user why instead of closing silently. Never throws.
/// </summary>
internal static partial class CrashLog
{
    public static string FilePath => Path.Combine(AppPaths.DataDirectory(), "crash.log");

    public static void InstallGlobalHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var path = Write("Unhandled error", e.ExceptionObject as Exception);
            if (e.IsTerminating) ShowNativeError(path);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("Unobserved background error", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>UI-thread errors are logged and kept from closing the window; Guardian only reads in that path.</summary>
    public static void InstallUiHandler() => Dispatcher.UIThread.UnhandledException += (_, e) =>
    {
        Write("UI error", e.Exception);
        e.Handled = true;
    };

    /// <summary>Appends the error and returns the log path (or null if even that failed).</summary>
    public static string? Write(string context, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory());
            var version = typeof(CrashLog).Assembly.GetName().Version;
            File.AppendAllText(FilePath,
                $"==== {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}  {context}  (version {version}, {RuntimeInformation.OSDescription}, {RuntimeInformation.OSArchitecture})\n{ex}\n\n");
            return FilePath;
        }
        catch
        {
            return null;
        }
    }

    public static string Explain(string? path) =>
        "AI Laptop Guardian could not start. Nothing on your computer was changed.\n\n" +
        (path is null ? "The error could not be written to a log file." : $"The error was saved to:\n{path}\n\nPlease send a photo of this message or that file.");

    /// <summary>Last-resort message when the window system itself is not running.</summary>
    public static void ShowNativeError(string? path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) MessageBoxW(IntPtr.Zero, Explain(path), "AI Laptop Guardian", 0x10);
            else Console.Error.WriteLine(Explain(path));
        }
        catch
        {
            // Nothing else can be done here.
        }
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
