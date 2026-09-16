using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MidiKeyPlayer.Persist;

namespace MidiKeyPlayer;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 先按设置把皮肤定下来，再建窗口：否则黑皮肤会先闪一帧白底。
            // 这里读一次设置只为了皮肤；MainWindow 自己还会读一次（各持一份，互不影响）。
            ThemeSwitch.Apply(AppConfig.Load().ThemeMode);

            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
