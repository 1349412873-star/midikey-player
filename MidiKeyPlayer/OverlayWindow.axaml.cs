using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MidiKeyPlayer.Engine;

namespace MidiKeyPlayer;

/// <summary>
/// 播放悬浮窗：置顶浮在目标程序画面上（默认屏幕右上角，可拖动，位置记忆）。
/// 倒计时期间显示大号秒数；开始演奏后显示整首曲子的迷你卷帘 ——
/// 音符按音高与时间排布，黄色播放头跟着进度走；暂停与循环遍数标在右下角。
/// 主窗负责它的生命周期：开始播放时 SetNotes + ShowProgress，停止 / 播完 / 关窗时 Close。
/// 不抢焦点（ShowActivated=false），不打扰目标程序。
/// </summary>
public partial class OverlayWindow : Window
{
    /// <summary>拖动松手后触发：参数是新的窗口位置（供主窗写进设置）。</summary>
    public event Action<int, int>? DragFinished;

    private static readonly IBrush NoteBrush = new SolidColorBrush(Color.Parse("#FF4CAF7D"));
    private const int MaxDrawnNotes = 2000;   // 音符太多时只画前这些，防止控件树爆炸

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

    /// <summary>
    /// 摆进整首曲子的音符（开始播放时调一次）。音符按 (音高, 时间) 画进迷你卷帘，
    /// 播放头位置由 <see cref="ShowProgress"/> 每拍更新。
    /// </summary>
    public void SetNotes(IReadOnlyList<MappedNote> notes, double totalSec)
    {
        // 清掉旧音符（播放头保留）
        for (int i = RollCanvas.Children.Count - 1; i >= 0; i--)
            if (!ReferenceEquals(RollCanvas.Children[i], Playhead))
                RollCanvas.Children.RemoveAt(i);

        double w = RollCanvas.Width, h = RollCanvas.Height;
        double total = Math.Max(0.1, totalSec);
        if (notes.Count == 0) return;

        int minP = int.MaxValue, maxP = int.MinValue;
        foreach (var n in notes)
        {
            if (n.Pitch < minP) minP = n.Pitch;
            if (n.Pitch > maxP) maxP = n.Pitch;
        }
        int rows = Math.Max(1, maxP - minP + 1);
        double rowH = h / rows;

        int limit = Math.Min(notes.Count, MaxDrawnNotes);
        for (int i = 0; i < limit; i++)
        {
            var n = notes[i];
            var r = new Avalonia.Controls.Shapes.Rectangle
            {
                Width = Math.Max(1.0, (n.End - n.Start) / total * w),
                Height = Math.Max(1.0, rowH - 0.6),
                Fill = NoteBrush,
            };
            Canvas.SetLeft(r, Math.Max(0, n.Start) / total * w);
            Canvas.SetTop(r, (maxP - n.Pitch) * rowH);
            RollCanvas.Children.Add(r);
        }
    }

    /// <summary>显示演奏进度：播放头跟着走，右下角标「已暂停 / 第 N 遍」。</summary>
    public void ShowProgress(double elapsed, double total, int loopCount, bool paused)
    {
        PanelCountdown.IsVisible = false;
        PanelProgress.IsVisible = true;
        Bar.Maximum = Math.Max(0.1, total);
        Bar.Value = Math.Clamp(elapsed, 0, Bar.Maximum);
        Canvas.SetLeft(Playhead, Math.Clamp(elapsed / Math.Max(0.1, total), 0, 1) * RollCanvas.Width);
        TxtTime.Text = $"{elapsed:F1} / {total:F1} s";

        string state = "";
        if (paused) state = "已暂停";
        if (loopCount > 0) state += (state.Length > 0 ? " · " : "") + $"第 {loopCount + 1} 遍";
        TxtState.Text = state;
        EnsureShown();
    }

    /// <summary>恢复上次拖到的位置；(-1, -1) 或屏幕外 = 默认屏幕右上角。</summary>
    public void RestorePosition(int x, int y)
    {
        if (_positioned) return;
        _positioned = true;

        if (Screens.Primary?.WorkingArea is not { } wa) return;

        // 先量出内容尺寸：刚 Show 时 Bounds 可能还是 0，用近似值兜底
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
