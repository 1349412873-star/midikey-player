using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MidiKeyPlayer.Engine;

namespace MidiKeyPlayer;

/// <summary>
/// 【开发用，可删】无显示器时把界面渲染成 PNG 再退出。
///
/// MIDIKEY_UI_SNAPSHOT=/path/main.png
///     在主窗打开后拍主界面（“快速上手”浮层先关掉拍一张、再打开拍一张）。
/// MIDIKEY_UI_SNAPSHOT_KEYMAP=/path/keymap.png
///     额外打开「键位设置」窗口并把它也拍成 PNG。
///
/// 两个变量可以只设一个；都不设则本文件无任何行为。
/// 删本文件时记得同时删 MainWindow 里的 InstallDevSnapshot(this)。
/// </summary>
public partial class MainWindow
{
    internal static void InstallDevSnapshot(MainWindow window)
    {
        var path = Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT");
        var keymapPath = Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_KEYMAP");
        if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(keymapPath)) return;

        window.Opened += (_, _) =>
        {
            // 无控制台时也能留证据：把诊断写到临时目录
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "midikey-snap.log"),
                    $"[{DateTime.Now:HH:mm:ss}] Opened；main={path ?? "(未设)"}；keymap={keymapPath ?? "(未设)"}\n");
            }
            catch { }
            // 等布局就绪，900ms 是实测够用的值
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    ReportRowHeights(window);

                    // “快速上手”浮层会盖住主界面：先关掉拍主界面，再开它拍浮层
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        if (window.QuickStartOverlay != null)
                            window.QuickStartOverlay.IsVisible = false;
                        CaptureMain(window, path!);

                        if (window.QuickStartOverlay != null)
                        {
                            window.QuickStartOverlay.IsVisible = true;
                            CaptureMain(window, Suffix(path!, "-quickstart"));
                        }
                        Console.WriteLine($"UI snapshot saved: {path}");
                    }

                    if (!string.IsNullOrWhiteSpace(keymapPath))
                    {
                        if (window.QuickStartOverlay != null)
                            window.QuickStartOverlay.IsVisible = false;
                        // 键位窗口的拍照与退出都在它自己的计时器里做：
                        // 这里绝不能立刻 Environment.Exit，否则那个计时器根本跑不到。
                        CaptureKeymap(window, keymapPath!);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("UI snapshot failed: " + ex);
                }
                Environment.Exit(0);
            };
            timer.Start();
        };
    }

    /// <summary>
    /// 拍图前把右栏三行的高度打到控制台，便于核对「卷帘是否拿回完整剩余高度」。
    /// 只在设了快照变量时运行，正常使用无输出、无行为。
    /// </summary>
    private static void ReportRowHeights(MainWindow window)
    {
        try
        {
            string line;
            if (window.RightColumn is not Grid g || g.RowDefinitions.Count < 3)
            {
                line = "[高度] 右栏 Grid 没找到";
            }
            else
            {
                double h0 = g.RowDefinitions[0].ActualHeight;
                double h1 = g.RowDefinitions[1].ActualHeight;
                double h2 = g.RowDefinitions[2].ActualHeight;
                string kind = window.OptionsArea?.GetType().Name ?? "缺失";
                double rollH = window.Roll?.Bounds.Height ?? -1;
                line = $"[高度] 右栏总高 {g.Bounds.Height:F0}；第0行(状态) {h0:F0}；"
                       + $"第1行(卷帘) {h1:F0}；第2行(选项) {h2:F0}；第2行根元素 {kind}；卷帘控件 {rollH:F0}";
            }
            Console.WriteLine(line);
            System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "midikey-snap.log"),
                line + "\n");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("row height report failed: " + ex.Message);
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "midikey-snap.log"),
                    "row height report failed: " + ex + "\n");
            }
            catch { }
        }
    }

    /// <summary>
    /// 把主窗内容渲染成 PNG。96 DPI + 与布局等大的画布，和真实窗口 1:1 对应
    /// （试过 192 DPI + 双倍画布，会被窗口 RenderScaling 再乘一次只剩左上角，别用）。
    /// </summary>
    private static void CaptureMain(MainWindow window, string path)
    {
        var root = (Visual?)window.Content ?? window;
        int w = Math.Max(1, (int)Math.Ceiling(root.Bounds.Width));
        int h = Math.Max(1, (int)Math.Ceiling(root.Bounds.Height));
        Console.WriteLine($"bounds={w}x{h} windowScaling={window.RenderScaling}");
        Save(root, path, new PixelSize(w, h), new Vector(96, 96));
    }

    /// <summary>
    /// 打开「键位设置」窗口并拍图。
    /// 用 Show() 而不是 ShowDialog()：模态对话框会一直阻塞到用户关闭，而这里拍完就要退出。
    /// 拍照走 DispatcherTimer（主窗快照用的就是这个办法，实测能跑到），不用 Post。
    /// </summary>
    private static void CaptureKeymap(MainWindow owner, string path)
    {
        var win = new KeymapWindow(KeymapProfile.Current ?? KeymapProfile.Default, null, null, null);
        win.Show(owner);
        Log($"键位窗口已打开 IsVisible={win.IsVisible}");

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Log("键位快照计时器触发");
            Shot(win, path);
            Environment.Exit(0);
        };
        timer.Start();

        // 双保险：万一 700ms 那个没跑到，1.5s 这个也要落盘并退出
        var guard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        guard.Tick += (_, _) =>
        {
            guard.Stop();
            Log("兜底计时器触发");
            Shot(win, path);
            Environment.Exit(0);
        };
        guard.Start();
    }

    private static void Shot(Window win, string path)
    {
        try
        {
            var root = (Visual?)win.Content ?? win;
            int w = Math.Max(1, (int)Math.Ceiling(root.Bounds.Width));
            int h = Math.Max(1, (int)Math.Ceiling(root.Bounds.Height));
            Log($"拍键位窗口 {w}x{h} IsVisible={win.IsVisible}");
            Save(root, path, new PixelSize(w, h), new Vector(96, 96));
            Log($"键位快照已写：{path} 存在={System.IO.File.Exists(path)}");
            Console.WriteLine($"Keymap snapshot saved: {path}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("keymap snapshot failed: " + ex);
            Log("keymap snapshot failed: " + ex);
        }
    }

    private static string LogPath() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "midikey-snap.log");

    /// <summary>无控制台（WinExe）时把诊断写进临时目录的日志文件。</summary>
    private static void Log(string line)
    {
        try { System.IO.File.AppendAllText(LogPath(), line + "\n"); } catch { }
    }

    private static void Save(Visual root, string path, PixelSize size, Vector dpi)
    {
        var rtb = new RenderTargetBitmap(size, dpi);
        rtb.Render(root);
        rtb.Save(path);
    }

    private static string Suffix(string path, string suffix)
    {
        int dot = path.LastIndexOf('.');
        return dot < 0 ? path + suffix : path[..dot] + suffix + path[dot..];
    }
}
