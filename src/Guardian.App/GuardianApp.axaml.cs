using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Guardian.App.ViewModels;
using Guardian.App.Views;

namespace Guardian.App;

public partial class GuardianApp : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        CrashLog.InstallUiHandler();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            GuardianHost? host = null;
            try
            {
                host = new GuardianHost();
                desktop.MainWindow = new MainWindow { DataContext = new MainViewModel(host) };
                desktop.ShutdownRequested += (_, _) => host.Dispose();
            }
            catch (Exception ex)
            {
                host?.Dispose();
                desktop.MainWindow = ErrorWindow(CrashLog.Explain(CrashLog.Write("Main window failed", ex)), ex.Message);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Built in code so it still shows if the main window's markup is what failed.</summary>
    private static Window ErrorWindow(string explanation, string error) => new()
    {
        Title = "AI Laptop Guardian",
        Width = 560,
        SizeToContent = SizeToContent.Height,
        WindowStartupLocation = WindowStartupLocation.CenterScreen,
        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                new SelectableTextBlock { Text = explanation, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new SelectableTextBlock { Text = "Error: " + error, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.7 },
            },
        },
    };
}
