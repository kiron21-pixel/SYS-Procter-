using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Guardian.App;
using Guardian.App.ViewModels;
using Guardian.App.Views;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

#pragma warning disable CS0618 // Bitmap.Save(string) is fine for test screenshots

namespace Guardian.App.Tests;

public static class HeadlessEntry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<GuardianApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

public class MainWindowTests
{
    private static void Capture(Window window, string path)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(path);
    }

    private static readonly HeadlessUnitTestSession Session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));

    [Fact]
    public async Task Window_opens_in_observe_only_with_file_moves_disabled()
    {
        var dataDir = Directory.CreateTempSubdirectory("guardian-ui-").FullName;
        await Session.Dispatch(async () =>
        {
            using var host = new GuardianHost(dataDir);
            var vm = new MainViewModel(host);
            var window = new MainWindow { DataContext = vm, Width = 1180, Height = 780 };
            window.Show();

            Assert.Equal("Observe Only", vm.ModeTitle);
            Assert.False(vm.CanMoveFiles);
            var move = window.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == "Move selected to holding area");
            Assert.False(move.IsEnabled);

            await vm.RunChecksCommand.ExecuteAsync(null);
            Assert.Contains("not a guarantee", vm.OverallSummary);

            var shot = Environment.GetEnvironmentVariable("GUARDIAN_SCREENSHOT");
            if (!string.IsNullOrEmpty(shot))
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Capture(window, shot);
                var tabs = window.GetLogicalDescendants().OfType<TabControl>().Single();
                tabs.SelectedIndex = 1;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Capture(window, shot.Replace(".png", "-protection.png"));
                tabs.SelectedIndex = 2;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Capture(window, shot.Replace(".png", "-security.png"));
                tabs.SelectedIndex = 6;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Capture(window, shot.Replace(".png", "-mode.png"));
            }
            window.Close();
            return true;
        }, CancellationToken.None);
    }
}
