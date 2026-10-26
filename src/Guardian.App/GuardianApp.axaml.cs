using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Guardian.App.ViewModels;
using Guardian.App.Views;

namespace Guardian.App;

public partial class GuardianApp : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var host = new GuardianHost();
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel(host) };
            desktop.ShutdownRequested += (_, _) => host.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
