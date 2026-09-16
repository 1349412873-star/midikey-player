using Avalonia.Controls;
using Avalonia.Input;

namespace MidiKeyPlayer;

/// <summary>
/// 播放悬浮窗：置顶浮在目标程序画面上（默认屏幕右上角，可拖动，位置记忆）。
/// 倒计时期间显示大号秒数；开始演奏后显示进度条、时间与当前音；暂停时标出「已暂停」。
/// 主窗负责它的生命周期：开始播放时 <see cref="ShowCountdown"/> / <see cref="ShowProgress"/>，
/// 停止 / 播完 / 关窗时 Close。不抢焦点（ShowActivated=false），不打扰目标程序。
/// </summary>
public partial class OverlayWindow : Window
{
    /// <summary>拖动松手后触发：参数是新的窗口位置（供主窗写进设置）。</summary>
    public event Action<int, int>? DragFinished;

    private bool _positioned;

    public OverlayWindow()
    {
        InitializeComponent();
    }

    /// <summary>显示倒计时剩几秒。</summary>
    public void ShowCountdown(int secondsLeft)
    {
        PanelCountdown.IsVisible = true;
        PanelProgress.IsVisible = false;
        TxtCountdown.Text = secondsLeft.ToString();
        EnsureShown();
    }

    /// <summary>显示演奏进度。note 为空时显示「演奏中…」。</summary>
    public void ShowProgress(double elapsed, double total, string note, int loopCount, bool paused)
    {
        PanelCountdown.IsVisible = false;
        PanelProgress.IsVisible = true;
        Bar.Maximum = Math.Max(0.1, total);
        Bar.Value = Math.Clamp(elapsed, 0, Bar.Maximum);
        TxtTime.Text = $"{elapsed:F1} / {total:F1} s";
        TxtNote.Text = string.IsNullOrEmpty(note) ? (paused ? "已暂停" : "演奏中…") : note;
        TxtLoop.Text = loopCount > 0 ? $"第 {loopCount + 1} 遍" : "";
        TxtPaused.IsVisible = paused;
        EnsureShown();
    }

    /// <summary>恢复上次拖到的位置；(-1, -1) 或屏幕外 = 默认屏幕右上角。</summary>
    public void RestorePosition(int x, int y)
    {
        if (_positioned) return;
        _positioned = true;

        if (Screens.Primary?.WorkingArea is not { } wa) return;

        // 先量出内容尺寸：刚 Show 时 Bounds 可能还是 0，用 ClientSize 兜底
        double w = Math.Max(Bounds.Width, 120);
        double h = Math.Max(Bounds.Height, 48);

        bool valid = x >= wa.X && y >= wa.Y && x + w <= wa.Right + 40 && y + h <= wa.Bottom + 40;
        if (valid)
        {
            Position = new Avalonia.PixelPoint(x, y);
        }
        else
        {
            Position = new Avalonia.PixelPoint((int)(wa.Right - w - 16), wa.Y + 16);
        }
    }

    private void EnsureShown()
    {
        if (!IsVisible) Show();
    }

    private void Card_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);   // 系统级拖动：松手在 PointerReleased 里回报位置
    }

    private void Card_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left)
            DragFinished?.Invoke(Position.X, Position.Y);
    }
}
