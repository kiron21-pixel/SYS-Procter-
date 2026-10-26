using Avalonia;

namespace Guardian.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--report"))
        {
            var dataDir = args.SkipWhile(a => a != "--data-dir").Skip(1).FirstOrDefault();
            return CliReport.RunAsync(args.Contains("--json"), dataDir).GetAwaiter().GetResult();
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<GuardianApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
