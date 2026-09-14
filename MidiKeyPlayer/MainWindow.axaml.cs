using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Selection;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Text;
using MidiKeyPlayer.Engine;
using MidiKeyPlayer.Input;
using MidiKeyPlayer.Midi;
using MidiKeyPlayer.Persist;
// Avalonia.Input 也有 KeyBinding（手势绑定），这里给键位方案的绑定起个别名，避免歧义
using KeyBinding = MidiKeyPlayer.Engine.KeyBinding;

namespace MidiKeyPlayer;

/// <summary>主窗口：选主旋律与播放控制、全局热键、进度跳转、实时变速/移调，托盘后台与设置记忆。</summary>
public partial class MainWindow : Window
{
    private readonly ObservableCollection<TrackRowVM> _tracks = new();
    private readonly List<TrackRowVM> _mixOrder = new();   // 勾选合奏的顺序 = 主次（先勾=主）

    private ParsedMidi? _parsed;
    private TrackRowVM? _selected;
    private List<MappedNote> _playNotes = new();
    private PlaybackEngine? _engine;
    private DispatcherTimer? _uiTimer;
    private DispatcherTimer? _countdownTimer;
    private DispatcherTimer? _liveTimer;
    private DispatcherTimer? _saveDeb;
    private readonly DispatcherTimer _previewDeb;
    private int _countdownLeft;
    private bool _busy;
    private bool _seeking;          // 用户正在拖进度条
    private List<MappedNote> _previewNotes = new();   // 全量音符（含超音域），供卷帘与定位使用
    private double _previewSeconds;                   // 未播放时的定位秒数
    private int _noteCount;                           // 当前谱面音符数（避免每次点击都重算）
    private readonly ScoreEditor _editor = new();     // 手动编辑后的谱面
    private bool _editing;                            // true = 用编辑结果，不再用自动提取
    private bool _helpOn;                             // 卷帘右侧操作说明：默认收起，保持界面干净
    private bool _liveQueued;       // 已排队待应用的实时移调
    private readonly LivePlayback _livePlay = new();   // MIDI 设备实时演奏（issue #4）
    private bool _midiStarting;     // 正在后台打开 MIDI 设备
    private IntPtr _gameHwnd;       // 播放期间记住的目标窗口（用于停止时把焦点还给它）
    private double _removedLeadSec; // “去除开头空拍”实际剪掉的秒数（本轮）
    private readonly AppConfig _cfg;
    private TrayIcon? _tray;
    private bool _quitNow;

    private static readonly int[] CountdownOptions = { 0, 3, 5, 10 };
    private const int FixedLeadMs = 25;
    private const double SeekStepSeconds = 5;   // 快进/后退热键的步长

    private bool _uiReady;   // 构造期各下拉框初始化会触发 *Changed，此时不应写日志
    private string _updateUrl = "";     // 有新版本时的下载页
    private string _updateTag = "";     // 有新版本时的版本号

    // —— 键位方案面板 ——
    private KeymapProfile _keymap = KeymapProfile.Default;   // 当前活动方案
    private bool _keymapLoading;      // 面板初始化 / 载入预设中，忽略 *Changed
    private bool _keyCapturing;       // 正在等用户按一个键来绑定
    private object? _keyCaptureTarget; // KeyBinding 或 "octaveUp"/"octaveDown"/"sharp"
    private bool _chordOn = true;     // 和弦开关：勾选 = 多声部合成；关闭 = 只留一条单音线

    public MainWindow()
    {
        InitializeComponent();

        TrackList.ItemsSource = _tracks;

        // 倒计时下拉：0/3/5/10 秒，默认 3 秒
        CountdownCombo.ItemsSource = new List<string> { "0秒(立即)", "3秒", "5秒", "10秒" };
        CountdownCombo.SelectedIndex = 1;

        // 统一控制热键下拉：F1..F12（默认 F6 = 开始/暂停/继续）
        var fkeys = new List<string> { "无" };
        for (int i = 1; i <= 12; i++) fkeys.Add("F" + i);
        HotkeyControlCombo.ItemsSource = fkeys;
        HotkeyControlCombo.SelectedIndex = 6;   // F6
        HotkeyRewindCombo.ItemsSource = fkeys;
        HotkeyRewindCombo.SelectedIndex = 5;    // F5
        HotkeyForwardCombo.ItemsSource = fkeys;
        HotkeyForwardCombo.SelectedIndex = 7;   // F7

        // 输入兼容档位：决定修饰键与音键之间的物理时间余量
        TimingCombo.ItemsSource = InputTiming.Names;
        TimingCombo.SelectedIndex = 1;          // 标准


        // —— 记住上次设置 ——
        _cfg = AppConfig.Load();
        CountdownCombo.SelectedIndex = Math.Clamp(_cfg.CountdownIndex, 0, 3);
        HotkeyControlCombo.SelectedIndex = Math.Clamp(_cfg.ControlHotkeyIndex, 0, 12);
        HotkeyRewindCombo.SelectedIndex = Math.Clamp(_cfg.RewindHotkeyIndex, 0, 12);
        HotkeyForwardCombo.SelectedIndex = Math.Clamp(_cfg.ForwardHotkeyIndex, 0, 12);
        SliderSpeed.Value = Math.Clamp(_cfg.Speed, 50, 200);
        SliderTranspose.Value = Math.Clamp(_cfg.Transpose, -10, 10);
        ChkTrimLead.IsChecked = _cfg.TrimLead;
        ChkAutoMinimize.IsChecked = _cfg.AutoMinimizeOnPlay;
        ChkChordMode.IsChecked = _cfg.ChordMode;
        TimingCombo.SelectedIndex = Math.Clamp(_cfg.TimingIndex, 0, 2);

        // 键位方案（Engine\KeymapProfile）：全局活动方案，实时演奏与文件播放共用
        _keymap = KeymapProfile.Current ?? KeymapProfile.Default;
        InitKeymapUi();
        KeyDown += OnWindowPreviewKeyDown;   // 键位录入：先于控件处理按键
        // 速度 / 移调 / 输入档位按方案名恢复：启动时按当前方案读一次，
        // 没有记录就用默认值（100% / 0 / 标准）；全局值同步进去，老设置文件照旧兼容
        ApplyProfileSettings(_keymap.Name);

        // MIDI 设备接入（issue #4）
        _livePlay.Timing = InputTiming.FromIndex(TimingCombo.SelectedIndex);
        _livePlay.BaseOctave = Math.Clamp(_cfg.MidiBaseOctave, 1, 6);
        _livePlay.MinVelocity = Math.Clamp(_cfg.MidiMinVelocity, 1, 127);
        _livePlay.AutoFit = _cfg.MidiAutoFit;
        SliderMidiOctave.Value = _livePlay.BaseOctave;
        SliderMidiVelocity.Value = _livePlay.MinVelocity;
        ChkMidiAutoFit.IsChecked = _livePlay.AutoFit;
        ChkMidiLive.IsChecked = false;   // 实时演奏默认关：设置里记住的设备名只用来预选
        UpdateMidiLabels();
        MidiInputService.NoteEvent += OnMidiNote;
        MidiInputService.Error += s => UiPost(() => InsertLog("[MIDI] " + s));
        _livePlay.Log += s => UiPost(() => InsertLog(s));
        _livePlay.NoteObserved += OnMidiObserved;
        RefreshMidiDevices();

        _saveDeb = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveDeb.Tick += (_, _) =>
        {
            _saveDeb!.Stop();
            SaveSettings();
        };

        if (Input.GlobalHotkeys.IsAvailable)
        {
            Input.GlobalHotkeys.Status += s => UiPost(() => InsertLog(s));
            Input.GlobalHotkeys.KeyState += OnGlobalKeyWorker;
            ReconfigureHotkeys();
            Input.GlobalHotkeys.Start();
        }

        // 捕获“已被滑块内部处理”的指针事件，实现任意位置点击/拖动跳转
        SliderProgress.AddHandler(InputElement.PointerPressedEvent, Progress_PointerPressed,
            RoutingStrategies.Bubble, handledEventsToo: true);
        SliderProgress.AddHandler(InputElement.PointerMovedEvent, Progress_PointerMoved,
            RoutingStrategies.Bubble, handledEventsToo: true);
        SliderProgress.AddHandler(InputElement.PointerReleasedEvent, Progress_PointerReleased,
            RoutingStrategies.Bubble, handledEventsToo: true);

        Roll.SeekPreview += OnRollPreview;
        Roll.SeekCommitted += OnRollSeek;
        Roll.SelectionChanged += UpdateEditUi;
        Roll.EditCommitted += OnRollEditCommitted;
        Roll.ViewChanged += OnRollViewChanged;
        ChkSnap.IsCheckedChanged += (_, _) => Roll.SnapEnabled = ChkSnap.IsChecked == true;
        ChkFollow.IsCheckedChanged += (_, _) => Roll.SetFollow(ChkFollow.IsChecked == true);
        KeyDown += OnWindowKeyDown;
        Roll.SnapEnabled = ChkSnap.IsChecked == true;
        Roll.SetFollow(ChkFollow.IsChecked == true);
        if (RollHelp != null) UpdateHelpVisibility();
        SizeChanged += (_, _) => UpdateHelpVisibility();

        _previewDeb = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _previewDeb.Tick += (_, _) =>
        {
            _previewDeb.Stop();
            RefreshPreview();
        };

        UpdateSettingLabels();
        UpdateVoiceRoles();
        RefreshPreview();
        SetIdleHint();
        UpdateTransportUi();
        _uiReady = true;

        // 启动即自检，状态行从一开始就有结论
        RunPreflight();

        // 后台静默查更新（不阻塞界面；没网就跳过）
        _ = CheckUpdateAsync();

        InsertLog("欢迎使用 MIDI 按键播放器");
        InsertLog("用法：打开 MIDI → 点一行作为主旋律 → 按 F6，倒计时内切到目标程序并装备乐器。");
        InsertLog("控制热键：F6 = 开始 / 暂停 / 继续（目标程序中生效，可改）。");
        if (OperatingSystem.IsWindows())
        {
            SetupTray();
            Opened += (_, _) => EnsureTray();
        }
        Opened += (_, _) => ShowQuickStartOnce();

        InstallDevSnapshot(this);   // 【开发用，可删】设了 MIDIKEY_UI_SNAPSHOT 才生效，见 DevUISnapshot.cs
    }

    // ================= 首次启动“快速上手” =================

    private void ShowQuickStartOnce()
    {
        if (_cfg == null || _cfg.FirstRunDone || QuickStartOverlay == null) return;
        QuickStartOverlay.IsVisible = true;
    }

    private void QuickStartOk_Click(object? sender, RoutedEventArgs e)
    {
        if (QuickStartOverlay != null) QuickStartOverlay.IsVisible = false;
        if (_cfg != null)
        {
            _cfg.FirstRunDone = true;
            _cfg.Save();
        }
    }

    // ================= 全局热键 =================

    private void Hotkey_Changed(object? sender, SelectionChangedEventArgs e)
    {
        ScheduleSave();
        if (!Input.GlobalHotkeys.IsAvailable) return;
        ReconfigureHotkeys();
    }

    private void CountdownCombo_Changed(object? sender, SelectionChangedEventArgs e)
    {
        ScheduleSave();
    }

    private void ReconfigureHotkeys()
    {
        var codes = new List<int>();
        foreach (var combo in new[] { HotkeyControlCombo, HotkeyRewindCombo, HotkeyForwardCombo })
        {
            int code = CodeOf(combo);
            if (code != 0) codes.Add(code);
        }
        Input.GlobalHotkeys.SetActive(codes);
    }

    /// <summary>下拉项 → 虚拟键码。索引 0 = 无。</summary>
    private static int CodeOf(ComboBox combo)
    {
        int idx = Math.Max(0, combo.SelectedIndex);
        return idx > 0 ? Input.GlobalHotkeys.FunctionKeyCode(idx) : 0;
    }

    private void OnGlobalKeyWorker(int code, bool down)
    {
        UiPost(() => HandleGlobalKey(code));
    }

    private void HandleGlobalKey(int code)
    {
        if (code == 0) return;
        if (code == CodeOf(HotkeyControlCombo)) { ToggleControl(); return; }
        if (code == CodeOf(HotkeyRewindCombo)) { SeekRelative(-SeekStepSeconds); return; }
        if (code == CodeOf(HotkeyForwardCombo)) SeekRelative(SeekStepSeconds);
    }

    /// <summary>
    /// 当前播放位置的唯一来源。三个播放器状态各有一套时间，混用就会取到过期的 0：
    /// 试听中取试听时钟，演奏中取引擎，都没有才取"起始位置"。
    /// </summary>
    private double CurrentPosition()
    {
        if (_previewOn) return PreviewNow();
        var eng = _engine;
        if (eng is { IsRunning: true }) return eng.ElapsedSeconds;
        return _previewSeconds;
    }

    /// <summary>当前总时长。与 <see cref="CurrentPosition"/> 必须取同一套状态，否则夹取会错。</summary>
    private double CurrentTotal()
    {
        if (_previewOn) return _previewTotal;
        var eng = _engine;
        if (eng is { IsRunning: true }) return Math.Max(0.001, eng.TotalSeconds);
        return PreviewTotalSeconds;
    }

    /// <summary>快进/后退固定步长：从当前位置相对移动。试听中、演奏中、空闲都可用。</summary>
    private void SeekRelative(double delta)
    {
        double total = CurrentTotal();
        if (total <= 0) return;
        double cur = Math.Clamp(CurrentPosition(), 0, total);
        double target = Math.Clamp(cur + delta, 0, total);
        ApplySeek(target);
        InsertLog($"{(delta < 0 ? "后退" : "前进")} {Math.Abs(delta):F0} 秒："
                  + $"{cur:F1} → {target:F1} s"
                  + (_previewOn ? "（试听）" : _engine is { IsRunning: true } ? "" : "（未播放，只改了起始位置）"));
    }

    /// <summary>统一控制键（默认 F6）：空闲=开始、倒计时中=取消、播放中=暂停、暂停中=继续；按钮与托盘项共用。</summary>
    private void ToggleControl()
    {
        var eng = _engine;
        if (eng is { IsRunning: true })
        {
            TogglePause();
            return;
        }
        if (_busy)   // 正在倒计时：再按一次 = 取消本次开始
        {
            InsertLog("已取消本次开始，可换好歌后再按一次。");
            _countdownTimer?.Stop();
            _countdownTimer = null;
            ResetUi();
            return;
        }
        RequestPlay();
    }

    private void TogglePause()
    {
        var eng = _engine;
        if (eng is not { IsRunning: true })
        {
            InsertLog("当前没有在播放，无法暂停。");
            return;
        }
        if (eng.IsPaused)
        {
            eng.Resume();
            LblStatus.Foreground = OkBrush;
            LblStatus.FontSize = 22;
            LblStatus.Text = "演奏中…";
        }
        else
        {
            eng.Pause();
            LblStatus.Foreground = FailBrush;
            LblStatus.Text = "已暂停 —— 按 F6 或点「▶ 继续」";
        }
        UpdateTransportUi();
    }

    // ================= 状态区 / 按钮提示 =================

    /// <summary>空闲时的状态区提示。</summary>
    private void SetIdleHint()
    {
        LblStatus.Foreground = NeutralBrush;
        LblStatus.FontSize = 15;
        LblStatus.Text = "打开 MIDI 并点选主旋律 → 按 F6 或点 ▶ 播放";
    }

    /// <summary>按状态切换播放按钮与热键提示；停止按钮在倒计时里也可用。</summary>
    private void UpdateTransportUi()
    {
        var eng = _engine;
        // 播放头跟随只在播放中生效，这里统一告知卷帘
        if (Roll != null) Roll.IsPlaying = eng is { IsRunning: true };
        if (eng is { IsRunning: true })
        {
            BtnPlay.IsEnabled = true;
            BtnStop.IsEnabled = true;
            BtnPlay.Content = eng.IsPaused ? "▶ 继续 (F6)" : "⏸ 暂停";
            TxtHotHint.Text = eng.IsPaused ? "F6 继续" : "F6 暂停";
            return;
        }
        BtnPlay.Content = "▶ 播放 (F6)";
        BtnStop.IsEnabled = _busy;   // 倒计时中允许点停止取消
        BtnPlay.IsEnabled = !_busy && ActiveRows().Count > 0 && BuildMapping().InRangeCount > 0;
        TxtHotHint.Text = "F6：开始 / 暂停 / 继续";
    }

    // ================= 日志 / 设置持久化 =================

    private void UiPost(Action a) => Dispatcher.UIThread.Post(a);

    private void InsertLog(string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        if (TxtLastMsg != null) TxtLastMsg.Text = msg;   // 界面只留最近一条
        Persist.LogFile.Append(line);                    // 完整历史仍然落盘
    }

    private void ScheduleSave()
    {
        _saveDeb?.Stop();
        _saveDeb?.Start();
    }

    private void SaveSettings()
    {
        if (_cfg == null) return;
        _cfg.Speed = (int)SliderSpeed.Value;
        _cfg.Transpose = (int)SliderTranspose.Value;
        _cfg.CountdownIndex = Math.Clamp(CountdownCombo.SelectedIndex, 0, 3);
        _cfg.ControlHotkeyIndex = Math.Clamp(HotkeyControlCombo.SelectedIndex, 0, 12);
        _cfg.RewindHotkeyIndex = Math.Clamp(HotkeyRewindCombo.SelectedIndex, 0, 12);
        _cfg.ForwardHotkeyIndex = Math.Clamp(HotkeyForwardCombo.SelectedIndex, 0, 12);
        _cfg.TrimLead = ChkTrimLead.IsChecked == true;
        _cfg.AutoMinimizeOnPlay = ChkAutoMinimize.IsChecked == true;
        _cfg.TimingIndex = Math.Clamp(TimingCombo.SelectedIndex, 0, 2);
        _cfg.MidiDeviceName = MidiDeviceCombo.SelectedItem as string ?? "";
        if (_cfg.MidiDeviceName.StartsWith('（')) _cfg.MidiDeviceName = "";
        _cfg.MidiLiveEnabled = ChkMidiLive.IsChecked == true;
        _cfg.MidiBaseOctave = (int)Math.Round(SliderMidiOctave.Value);
        _cfg.MidiMinVelocity = (int)Math.Round(SliderMidiVelocity.Value);
        _cfg.MidiAutoFit = ChkMidiAutoFit.IsChecked == true;
        RememberCurrentProfileSettings();   // 速度 / 移调 / 输入档位按方案名同时记一份
        _cfg.Save();
    }

    private void UpdateSettingLabels()
    {
        if (TxtSpeed is null || TxtTranspose is null) return;
        TxtSpeed.Text = $"{SliderSpeed.Value:0}%";
        double tr = SliderTranspose.Value;
        TxtTranspose.Text = tr > 0 ? $"+{tr:0}" : $"{tr:0}";
    }

    // ================= 速度 / 移调 / 输入档位「按键位方案分别记忆」 =================
    //
    // 用户要求：每个键位方案单独记住速度、移调、输入兼容档。存储由 AppConfig 负责
    // （SettingsFor / RememberProfile），这里只做接线：换方案时「先存旧、再读新」。
    // 全局字段仍然照写（SaveSettings），老设置文件照旧能读。

    /// <summary>把当前速度 / 移调 / 输入档位写回当前方案名下。</summary>
    private void RememberCurrentProfileSettings()
    {
        if (_cfg == null) return;
        _cfg.RememberProfile(_keymap.Name,
            (int)SliderSpeed.Value,
            (int)SliderTranspose.Value,
            Math.Clamp(TimingCombo.SelectedIndex, 0, 2));
    }

    /// <summary>
    /// 按方案名读出速度 / 移调 / 输入档位并应用到界面与内部字段。
    /// 没有记录时 AppConfig 会按当前全局值建一条，首次启动的全局值就是默认值（100% / 0 / 标准）。
    /// </summary>
    private void ApplyProfileSettings(string? profileName)
    {
        if (_cfg == null) return;
        var s = _cfg.SettingsFor(profileName);

        SliderSpeed.Value = Math.Clamp(s.Speed, 50, 200);        // 滑块自身的范围
        SliderTranspose.Value = Math.Clamp(s.Transpose, -10, 10);
        TimingCombo.SelectedIndex = Math.Clamp(s.TimingIndex, 0, 2);

        // 档位跟着改：播放引擎与实时演奏都要用新的时序预算
        var timing = InputTiming.FromIndex(TimingCombo.SelectedIndex);
        _livePlay.Timing = timing;

        // 全局字段一并同步，保证旧设置文件与旧版本程序读到的还是这套值
        _cfg.Speed = (int)SliderSpeed.Value;
        _cfg.Transpose = (int)SliderTranspose.Value;
        _cfg.TimingIndex = TimingCombo.SelectedIndex;

        UpdateSettingLabels();
    }

    private void SpeedChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateSettingLabels();

        if (_engine is { IsRunning: true } eng)
        {
            if (ReferenceEquals(sender, SliderSpeed))
            {
                // 播放中实时变速：立即生效
                eng.Speed = SliderSpeed.Value / 100.0;
            }
            else if (ReferenceEquals(sender, SliderTranspose))
            {
                // 播放中实时移调：等拖动停顿 150ms 再换谱，避免逐刻度反复重建
                QueueLiveTranspose();
            }
            return;
        }

        if (!_busy && _previewDeb is not null)
        {
            _previewDeb.Stop();
            _previewDeb.Start();
        }
        ScheduleSave();
    }

    /// <summary>播放中移调：停顿后重建剩余音符并应用到引擎。</summary>
    private void QueueLiveTranspose()
    {
        if (_liveQueued) return;
        _liveQueued = true;
        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _liveTimer.Tick += (_, _) =>
        {
            _liveTimer!.Stop();
            _liveTimer = null;
            _liveQueued = false;
            ApplyLiveTranspose();
        };
        _liveTimer.Start();
    }

    private void ApplyLiveTranspose()
    {
        var eng = _engine;
        if (eng == null || _selected == null) return;

        var map = BuildMapping();
        var notes = map.Notes.Where(n => n.InRange).ToList();
        eng.UpdateNotes(notes);
        InsertLog($"移调 {CurrentTranspose:+#;-#;0}：可演奏 {notes.Count} 音" +
                  (map.SkipCount > 0 ? $" / 空拍 {map.SkipCount}" : ""));
        RefreshPreview();
    }

    // ================= 进度条拖拽（播放中跳转） =================

    private void Progress_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!SliderProgress.IsEnabled) return;
        _seeking = true;
        SeekThumbTo(e);            // 点哪跳到哪（不用先抓滑块）
        PreviewSeekFromSlider();
    }

    private void Progress_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_seeking) return;
        SeekThumbTo(e);            // 按住拖动 = 预览位置（不打断播放）
        PreviewSeekFromSlider();
    }

    private void Progress_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_seeking) return;
        _seeking = false;
        ApplySeek(SliderProgress.Value);
    }

    private void SeekThumbTo(PointerEventArgs e)
    {
        double w = SliderProgress.Bounds.Width;
        if (w <= 0) return;
        double x = e.GetPosition(SliderProgress).X;
        double frac = Math.Clamp(x / w, 0.0, 1.0);
        SliderProgress.Value = frac * SliderProgress.Maximum;
    }

    /// <summary>拖动进度条时只更新显示，松手才跳转。</summary>
    private void PreviewSeekFromSlider() => ShowPosition(SliderProgress.Value);

    /// <summary>卷帘拖动中：只挪指针与音符显示，不打断播放。</summary>
    private void OnRollPreview(double seconds)
    {
        _seeking = true;          // 让 UI 定时器别把指针拽回去
        ShowPosition(seconds);
    }

    /// <summary>卷帘松手：真正跳转。</summary>
    private void OnRollSeek(double seconds)
    {
        _seeking = false;
        ApplySeek(seconds);
    }

    /// <summary>定位到某个秒数：试听中跳转试听，演奏中跳转引擎，都没有只记住位置。</summary>
    private void ApplySeek(double seconds)
    {
        if (_previewOn)
        {
            PreviewSeekTo(seconds);
            ShowPosition(seconds);
            return;
        }

        var eng = _engine;
        if (eng is { IsRunning: true })
        {
            double total = Math.Max(0.001, eng.TotalSeconds);
            eng.SeekFraction(Math.Clamp(seconds / total, 0, 1));
        }
        else
        {
            _previewSeconds = Math.Clamp(seconds, 0, PreviewTotalSeconds);
        }
        ShowPosition(seconds);
    }

    private double PreviewTotalSeconds =>
        _previewNotes.Count == 0 ? 0 : _previewNotes.Max(n => n.End);

    /// <summary>进度条/卷帘/时间/定位音一起摆到某个秒数（不动引擎，也不动试听时钟）。</summary>
    private void ShowPosition(double seconds)
    {
        double total = CurrentTotal();
        double t = Math.Clamp(seconds, 0, total);
        SliderProgress.Value = t;
        Roll.SetPosition(t);
        TxtTime.Text = $"{t:F1} / {total:F1} s";
        UpdateSeekNote(t);
        // 空闲时把位置记下来。否则 F5/F7 取到过期的 0，表现就是"F7 跳到 5 秒、F5 回开头"。
        if (!_previewOn && _engine is not { IsRunning: true }) _previewSeconds = t;
    }

    /// <summary>显示某个时刻的音：音名 + 简谱 + 要按的键。不传则取当前指针位置。</summary>
    private void UpdateSeekNote(double? atSeconds = null)
    {
        if (TxtSeekNote == null) return;
        double t = atSeconds ?? (_engine is { IsRunning: true } ? _engine.ElapsedSeconds : _previewSeconds);
        MappedNote? note = _previewNotes.LastOrDefault(n => n.Start <= t && t < n.End);
        if (note == null) { TxtSeekNote.Text = "—"; return; }
        string name = Music.NoteName(note.Pitch);
        if (!note.InRange) { TxtSeekNote.Text = $"{name} 超音域"; return; }
        // 按键与修饰键一律按当前键位方案算，和实际演奏一致
        _keymap.TryKeyOfPitch(note.Pitch, out string key, out int octaveOffset, out bool sharp);
        var parts = new List<string>();
        if (octaveOffset < 0) parts.Add("降八度键+");
        else if (octaveOffset > 0) parts.Add("升八度键+");
        if (sharp) parts.Add("升半音键+");
        parts.Add(string.IsNullOrEmpty(key) ? "（无按键）" : DisplayKey(key));
        TxtSeekNote.Text = $"{name} {Music.DegreeName(note.Pitch)} · {string.Join("", parts)}";
    }

    /// <summary>按键名显示：逗号写成全角逗号，其余原样。</summary>
    private static string DisplayKey(string key) => key == "," ? "，" : key;

    // ================= 卷帘编辑 =================

    /// <summary>谱面来源要变了：有手动改动就先丢弃并说明，否则用户会以为点了没反应。</summary>
    private void DropEditsIfAny(string why)
    {
        if (!_editing) return;
        ResetEdits();
        InsertLog($"已丢弃手动改动（{why}）。");
    }

    /// <summary>第一次编辑时，把当前自动结果冻结成可编辑谱面。</summary>
    private void BeginEditIfNeeded()
    {
        if (_editing) return;
        _editor.Reset(ComputeAutoNotes());
        _editing = true;
        InsertLog("已进入编辑模式：自动提取的选项不再影响谱面，点「还原为自动」可退出。");
    }

    /// <summary>换歌或点「还原为自动」时丢弃全部手动改动。</summary>
    private void ResetEdits()
    {
        _editing = false;
        _editor.Clear();
        Roll.ClearSelection();
    }

    /// <summary>
    /// 卷帘完成一次编辑手势，提交的是**整条新谱面**（而不是单个音的增量）。
    /// 这样拖动一组音在撤销栈里只算一步；卷帘自己已经持有这份数据，
    /// 所以这里不再把音符推回给它（推回去会重置视口与选择）。
    /// </summary>
    private void OnRollEditCommitted(IReadOnlyList<RawNote> notes, string what)
    {
        BeginEditIfNeeded();
        _editor.ReplaceAll(notes);
        RefreshPreview(pushToRoll: false);
        InsertLog($"已{what}。");
    }

    /// <summary>卷帘视口/缩放/跟随变化：刷新工具栏读数。</summary>
    private void OnRollViewChanged()
    {
        if (TxtZoom == null) return;
        TxtZoom.Text = $"{Roll.ZoomPercent:F0}%";
        if (ChkFollow != null && ChkFollow.IsChecked != Roll.FollowPlayhead)
            ChkFollow.IsChecked = Roll.FollowPlayhead;
    }

    /// <summary>加音用的默认长度：取现有音符的中位长度，夹在 0.1-1.0 秒。</summary>
    private double MedianNoteLength()
    {
        var src = _editing ? _editor.Notes : ComputeAutoNotes();
        var lens = src.Select(n => n.End - n.Start).Where(l => l > 0.02).OrderBy(l => l).ToList();
        if (lens.Count == 0) return 0.25;
        return Math.Clamp(lens[lens.Count / 2], 0.1, 1.0);
    }

    private void DoUndo()
    {
        if (!_editing || !_editor.Undo()) { InsertLog("没有可撤销的操作。"); return; }
        RefreshPreview(keepView: true);
        Roll.ClearSelection();
        InsertLog("已撤销。");
    }

    private void DoRedo()
    {
        if (!_editing || !_editor.Redo()) { InsertLog("没有可重做的操作。"); return; }
        RefreshPreview(keepView: true);
        Roll.ClearSelection();
        InsertLog("已重做。");
    }

    /// <summary>删除卷帘里选中的音（工具栏按钮 / Delete 键）。</summary>
    private void DeleteSelectedNote()
    {
        if (!Roll.HasSelection) { InsertLog("先在卷帘上点一个音（或框选几个），再删除。"); return; }
        Roll.DeleteSelected();
    }

    private void UpdateEditUi()
    {
        if (BtnUndo == null) return;
        BtnUndo.IsEnabled = _editing && _editor.CanUndo;
        BtnRedo.IsEnabled = _editing && _editor.CanRedo;
        BtnDeleteNote.IsEnabled = Roll.HasSelection;
        BtnResetEdits.IsEnabled = _editing;
        BtnExportMidi.IsEnabled = _noteCount > 0;
    }

    private void Undo_Click(object? sender, RoutedEventArgs e) => DoUndo();
    private void ZoomIn_Click(object? sender, RoutedEventArgs e) => Roll.ZoomCenter(0.6);
    private void ZoomOut_Click(object? sender, RoutedEventArgs e) => Roll.ZoomCenter(1.67);
    private void ZoomFit_Click(object? sender, RoutedEventArgs e) => Roll.FitAll();
    private void Redo_Click(object? sender, RoutedEventArgs e) => DoRedo();
    private void DeleteNote_Click(object? sender, RoutedEventArgs e) => DeleteSelectedNote();

    private void Snap_Changed(object? sender, RoutedEventArgs e)
    {
        if (Roll != null) Roll.SnapEnabled = ChkSnap.IsChecked == true;
    }

    /// <summary>帮助按钮：显示 / 收起卷帘右侧的操作说明。</summary>
    private void Help_Click(object? sender, RoutedEventArgs e)
    {
        _helpOn = !_helpOn;
        UpdateHelpVisibility();
    }

    /// <summary>
    /// 说明栏占 188px。窗口太窄时，右栏减去它就不够放卷帘工具栏，缩放按钮会被挤掉 ——
    /// 所以按窗口宽度自动收起，拉宽后自动恢复。
    /// </summary>
    private void UpdateHelpVisibility()
    {
        if (RollHelp == null) return;
        bool roomy = Bounds.Width >= 1080;
        RollHelp.IsVisible = _helpOn && roomy;
        if (BtnHelp != null)
        {
            BtnHelp.IsEnabled = roomy;
            ToolTip.SetTip(BtnHelp, roomy
                ? (_helpOn ? "收起操作说明，把宽度让给卷帘" : "显示操作说明")
                : "窗口太窄：说明已自动收起，避免把缩放按钮挤掉。把窗口拉宽就会恢复。");
        }
    }

    // ================= 内置试听 =================
    //
    // 试听是一个**独立按钮**，和演奏完全无关：不发按键、不走倒计时、不最小化窗口。
    // 关键在于它是"事件调度"而不是"按固定间隔采样"：
    // 播放前先把每个音的 note-on / note-off 时刻排成一张表（按真实秒），
    // 定时器每次醒来把**所有到点的**事件一次发完。这样即使定时器被 UI 卡住、
    // 或者某个音短于定时器间隔，也不会被漏掉 —— 采样式实现会成片吞音。

    private MidiPreview? _preview;
    private DispatcherTimer? _previewTimer;
    private readonly List<(double T, int Pitch, bool Down)> _previewEvents = new();
    /// <summary>每个音在"真实秒"下的起止，用于跳转时判断"跳进了哪个音的中间"。</summary>
    private readonly List<(double S, double E, int Pitch)> _previewSpans = new();
    private double _previewTotal;
    private int _previewNext;
    /// <summary>试听位置的时间基准：位置 = (现在 - 基准) / 频率。跳转只需挪这个基准。</summary>
    private long _previewBaseTicks;
    private bool _previewOn;
    private readonly HashSet<int> _previewSounding = new();

    /// <summary>试听当前所在秒数（真实秒，已含速度）。</summary>
    private double PreviewNow() =>
        (System.Diagnostics.Stopwatch.GetTimestamp() - _previewBaseTicks)
        / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>
    /// 试听中跳转到某个秒数：挪时间基准、放开所有在响的音、把事件游标移到该点之后，
    /// 并且把"跳进去的那个音"补上（否则从音符中间跳过去这段就是哑的）。
    /// </summary>
    private void PreviewSeekTo(double seconds)
    {
        double t = Math.Clamp(seconds, 0, _previewTotal);
        _previewBaseTicks = System.Diagnostics.Stopwatch.GetTimestamp()
                            - (long)(t * System.Diagnostics.Stopwatch.Frequency);

        _preview?.StopAll();
        _previewSounding.Clear();

        _previewNext = 0;
        while (_previewNext < _previewEvents.Count && _previewEvents[_previewNext].T < t) _previewNext++;

        if (_preview != null)
        {
            foreach (var s in _previewSpans)
            {
                if (s.S <= t && t < s.E)
                {
                    _preview.PlayNote(s.Pitch, velocity: 96, autoRelease: false);
                    _previewSounding.Add(s.Pitch);
                }
            }
        }
    }

    /// <summary>试听按钮：点一下开始放声音，再点一下停止。</summary>
    private void Preview_Click(object? sender, RoutedEventArgs e)
    {
        if (_previewOn) StopPreviewAudio();
        else StartPreviewAudio();
    }

    private void StartPreviewAudio()
    {
        // 不能用 _playNotes：那个只在"开始播放"时才填。用户刚打开文件就点试听时它是空的。
        if (_busy || _engine is { IsRunning: true })
        {
            InsertLog("试听与演奏不能同时进行，先停止当前演奏。");
            return;
        }
        var notes = BuildMapping().Notes.Where(n => n.InRange).ToList();
        if (notes.Count == 0)
        {
            InsertLog("当前谱面没有可演奏的音，无法试听。先选一行主旋律。");
            return;
        }

        if (_preview == null)
        {
            _preview = new MidiPreview();
            if (!_preview.IsAvailable)
            {
                InsertLog($"试听不可用：{_preview.LastError}。" +
                          "系统可能没有可用的 MIDI 输出设备（正常应有 Microsoft GS Wavetable Synth）。");
                _preview.Dispose();
                _preview = null;
                BtnPreview.IsEnabled = false;
                return;
            }
        }

        // 与演奏同一套时间基准：谱面时间除以速度 = 真实秒
        double speed = Math.Max(0.1, SliderSpeed.Value / 100.0);
        const double gap = 0.02;   // 同音高重复时留出断开，否则不会重新触发
        _previewEvents.Clear();
        _previewSpans.Clear();
        foreach (var n in notes)
        {
            double s = n.Start / speed;
            double e = n.End / speed;
            double off = Math.Max(s + 0.03, e - gap);
            _previewEvents.Add((s, n.Pitch, true));
            _previewEvents.Add((off, n.Pitch, false));
            _previewSpans.Add((s, off, n.Pitch));
        }
        _previewEvents.Sort((a, b) => a.T.CompareTo(b.T));

        _previewNext = 0;
        _previewSounding.Clear();
        _previewTotal = _previewEvents.Count == 0 ? 0 : _previewEvents[^1].T;
        // 从进度条当前位置开始试听（用户可能已经把指针拖到某处了）
        double startAt = Math.Clamp(SliderProgress.Value, 0, _previewTotal);
        _previewBaseTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        _previewOn = true;
        BtnPreview.Content = "⏹ 停止试听";
        SliderProgress.Maximum = Math.Max(0.1, _previewTotal);
        PreviewSeekTo(startAt);

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        _previewTimer.Tick += (_, _) => PreviewTick();
        _previewTimer.Start();

        // 试听也是"在播放"：让卷帘自动跟随播放头。少了这句，视口不滚动，
        // 播放头很快就跑出画面，看起来就像"画面停在原地、声音已经走远"。
        if (Roll != null)
        {
            Roll.IsPlaying = true;
            // 卷帘时间轴比谱面末音长一点（留了右键余量）：换算比例，让播放头与音符严格对齐。
            Roll.SetPlayheadScale(_previewTotal > 0 ? PreviewTotalSeconds / _previewTotal : 1.0);
        }

        InsertLog($"试听开始：{notes.Count} 个音，约 {_previewTotal:F1}s（不发送按键）");
    }

    private void PreviewTick()
    {
        if (!_previewOn || _preview == null) return;

        // 用户正在拖进度条/卷帘：这一帧不推进也不覆盖，把画面交给拖动
        if (_seeking) return;

        double t = PreviewNow();

        // 一次补齐所有到点的事件（不是只看"当前这一刻"，所以不会漏音）
        while (_previewNext < _previewEvents.Count && _previewEvents[_previewNext].T <= t)
        {
            var e = _previewEvents[_previewNext++];
            if (e.Down)
            {
                _preview.PlayNote(e.Pitch, velocity: 96, autoRelease: false);
                _previewSounding.Add(e.Pitch);
            }
            else
            {
                _preview.StopNote(e.Pitch);
                _previewSounding.Remove(e.Pitch);
            }
        }

        double shown = Math.Min(t, _previewTotal);
        if (_previewTotal > 0)
        {
            SliderProgress.Value = shown;
            TxtTime.Text = $"{shown:F1} / {_previewTotal:F1} s";
            Roll.SetPosition(shown);
            UpdateSeekNote(shown);
        }

        if (_previewNext >= _previewEvents.Count) StopPreviewAudio();
    }

    /// <summary>停止试听：放掉所有正在响的音，恢复按钮文字。</summary>
    private void StopPreviewAudio()
    {
        if (!_previewOn && _previewTimer == null) return;
        _previewOn = false;
        _previewTimer?.Stop();
        _previewTimer = null;
        _previewSounding.Clear();
        _preview?.StopAll();
        if (BtnPreview != null) BtnPreview.Content = "试听";
        // 试听结束就不再跟随播放头（演奏中的状态由 UpdateTransportUi 负责）
        if (Roll != null && _engine is not { IsRunning: true })
        {
            Roll.IsPlaying = false;
            Roll.SetPlayheadScale(1.0);
        }
    }

    private void ResetEdits_Click(object? sender, RoutedEventArgs e)
    {
        if (!_editing) { InsertLog("当前就是自动结果，没有可还原的改动。"); return; }
        ResetEdits();
        RefreshPreview();
        InsertLog("已还原为自动提取结果。");
    }

    /// <summary>窗口级快捷键：删除、撤销、重做。卷帘不必先获得焦点。</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (ctrl && e.Key == Key.Z)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) DoRedo(); else DoUndo();
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.Y) { DoRedo(); e.Handled = true; return; }
        if (e.Key == Key.Delete) { DeleteSelectedNote(); e.Handled = true; }
    }

    /// <summary>把当前谱面写成标准 MIDI 文件。</summary>
    private async void ExportMidi_Click(object? sender, RoutedEventArgs e)
    {
        var raw = GetActiveRawNotes();
        if (raw.Count == 0) { InsertLog("没有音符可导出。"); return; }
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出编辑后的 MIDI",
                SuggestedFileName = SuggestMidiName(),
                DefaultExtension = "mid",
                FileTypeChoices = new List<FilePickerFileType>
                {
                    new("MIDI 文件") { Patterns = new List<string> { "*.mid" } }
                }
            });
            if (file == null) return;
            string? path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            MidiExporter.Write(path, raw, "MidiKeyPlayer 编辑");
            InsertLog($"已导出 MIDI：{raw.Count} 个音 → {System.IO.Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            InsertLog($"导出 MIDI 失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private string SuggestMidiName()
    {
        string name = "edited";
        if (_parsed != null && !string.IsNullOrEmpty(_parsed.FilePath))
            name = System.IO.Path.GetFileNameWithoutExtension(_parsed.FilePath) + "-edited";
        return name + ".mid";
    }

    /// <summary>没有可定位的谱面时，清空进度条、卷帘与音符显示。</summary>
    private void ResetSeekUi()
    {
        Roll.SetNotes(Array.Empty<RawNote>(), Array.Empty<int>(), 0);
        Roll.SetPosition(0);
        SliderProgress.Maximum = 0.1;
        SliderProgress.Value = 0;
        SliderProgress.IsEnabled = false;
        TxtTime.Text = "0.0 / 0.0 s";
        TxtSeekNote.Text = "—";
    }

    // ================= 文件载入 =================

    private async void BtnOpen_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择 MIDI 文件",
                AllowMultiple = false,
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new("MIDI 文件")
                    {
                        Patterns = new List<string> { "*.mid", "*.midi", "*.kar", "*.rmi" }
                    },
                    new("所有文件") { Patterns = new List<string> { "*.*" } }
                }
            });
            if (files.Count == 0) return;

            var path = files[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            // 暂停/播放中也能换歌：先停掉当前播放，避免新旧串曲
            StopPlaybackForNewFile();

            try
            {
                var parsed = MidiLoader.Parse(path);
                _parsed = parsed;
                _tracks.Clear();
                _mixOrder.Clear();
                foreach (var c in parsed.Candidates) _tracks.Add(new TrackRowVM(c));

                LblFile.Text = System.IO.Path.GetFileName(path);
                InsertLog($"已载入 {System.IO.Path.GetFileName(path)}：{parsed.Candidates.Count} 个候选，时长 ≈ {parsed.DurationSec:F1}s");

                _selected = null;
                _previewSeconds = 0;      // 换歌必须回到 0，否则上一首的位置会夹到新曲末尾 → 一播放就结束
                Roll.FitAll();
                ResetEdits();
                ChooseRecommendedTrack();
                RefreshPreview();
            }
            catch (Exception ex)
            {
                var parts = new List<string>();
                Exception? inner = ex;
                while (inner != null)
                {
                    parts.Add($"{inner.GetType().Name}: {inner.Message}");
                    inner = inner.InnerException;
                }
                InsertLog($"载入失败：{path}");
                InsertLog($"  原因：{string.Join("  <-  ", parts)}（错误码 0x{ex.HResult:X8}）");
            }
        }
        catch (Exception ex)
        {
            InsertLog($"打开文件对话框失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>换歌前停掉旧曲（松开按键、释放引擎）。</summary>
    private void StopPlaybackForNewFile()
    {
        if (_engine is not { IsRunning: true }) return;
        StopPlaybackNow();
        InsertLog("已停止当前播放（换歌）。");
    }

    // ================= 主旋律选择 =================

    /// <summary>载入后自动挑最像主旋律的轨并选中。依据：非打击乐、轨名像旋律、音域贴合乐器。</summary>
    private void ChooseRecommendedTrack()
    {
        TrackRowVM? best = null;
        double bestScore = double.MinValue;
        foreach (var r in _tracks)
        {
            if (!r.IsPlayable) continue;
            double s = ScoreCandidate(r);
            if (s > bestScore)
            {
                bestScore = s;
                best = r;
            }
        }
        if (best == null)
        {
            InsertLog("没有适合乐器的旋律轨（全是打击乐？），请手动点选一行。");
            return;
        }

        best.IsRecommended = true;
        TrackList.SelectedItem = best;   // 触发 SelectionChanged → SetMain → 高亮
        if (bestScore >= 20)
            InsertLog($"已自动选中推荐轨：{best.DisplayName}（想换就点其它行）");
        else
            InsertLog($"已自动选中较合适的轨：{best.DisplayName}（音域贴合不多，可用「一键移调」）");

        // 覆盖不全就明说：进度条与卷帘只覆盖这一段，免得用户以为「加载不全」
        double span = best.Candidate.Notes.Count == 0 ? 0 : best.Candidate.Notes.Max(n => n.End);
        double fileSec = _parsed?.DurationSec ?? 0;
        if (fileSec > 5 && span < fileSec * 0.6)
            InsertLog($"注意：这条轨只到 {span:F1}s，全曲 {fileSec:F1}s。" +
                      $"进度条与卷帘只覆盖这一段，可在左侧点其它行换轨。");
    }

    private double ScoreCandidate(TrackRowVM r)
    {
        string name = r.Candidate.Name;
        double s = 0;

        // 轨道名像“旋律”的加分
        string[] melodyHints =
            { "旋律", "主旋律", "主唱", "人声", "女声", "男声", "独奏", "主音",
              "lead", "melod", "vocal", "vox", "solo", "sing" };
        foreach (var kw in melodyHints)
        {
            if (name.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                s += 45;
                break;
            }
        }
        // 明显是伴奏/低音/吉他的减分
        string[] accompHints =
            { "伴奏", "和声", "和弦", "低音", "吉他", "钢琴伴", "节奏",
              "bass", "chord", "back", "guitar", "pad", "rhythm", "fx" };
        foreach (var kw in accompHints)
        {
            if (name.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                s -= 35;
                break;
            }
        }

        var notes = r.Candidate.Notes;
        if (notes.Count > 0)
        {
            var map = NoteMapper.Map(notes, 0, null);
            s += 30.0 * map.InRangeCount / notes.Count;   // 音域贴合度（不抢先于名字线索）
            if (notes.Count < 8) s -= 20;                  // 太碎不像是能演奏的歌
            s += Math.Min(notes.Count / 50.0, 8.0);        // 稍偏好完整曲目轨

            // 覆盖时长：只盖住开头几秒的轨（前奏、过门、演示音）不该压过整首主旋律。
            // 用「最后一个音的结束时刻」而不是跨度，这样后半段才进旋律的轨也能得高分。
            double fileSec = _parsed?.DurationSec ?? 0;
            if (fileSec > 1)
            {
                double cover = Math.Clamp(notes.Max(n => n.End) / fileSec, 0, 1);
                s += 60.0 * cover * cover;
                if (cover < 0.25) s -= 25;
            }
        }
        return s;
    }

    private void TrackList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TrackList.SelectedItem is TrackRowVM row) SetMain(row);
    }

    private void SetMain(TrackRowVM? row)
    {
        if (row == null) return;
        DropEditsIfAny("换了主旋律轨");
        foreach (var r in _tracks) r.IsMain = ReferenceEquals(r, row);
        _selected = row;
        UpdateVoiceRoles();   // 未勾合奏时主旋律轨就是 0 号色
        RefreshPreview();
    }

    // ================= 映射与预览 =================

    private int CurrentTranspose => (int)SliderTranspose.Value;

    /// <summary>要演奏的行：勾了“合”就按勾选顺序合奏，否则用点选的那一行。</summary>
    private List<TrackRowVM> ActiveRows()
    {
        var mix = _mixOrder.Where(r => r.IsMix).ToList();
        if (mix.Count > 0) return mix;
        return _selected != null ? new List<TrackRowVM> { _selected } : new List<TrackRowVM>();
    }

    // ================= 声轨颜色一一对应 =================
    //
    // 颜色号 = 该轨在这次演奏里的声部序号：勾了「合」按勾选顺序 0 起，否则主旋律轨 = 0。
    // 左侧列表的文字用它上色，卷帘的音符也用它上色，所以两边一一对应。
    // 色值只来自 Styles\Theme.axaml 的 BrushVoice0..11；未参与合奏降不透明度，打击乐轨用灰。

    /// <summary>本轨在这次演奏里的声部序号；-1 = 不参与。</summary>
    private int VoiceIndexOf(TrackRowVM row)
    {
        if (!row.IsVoiceActive || row.IsPercussion) return -1;
        if (row.IsMix)
        {
            int i = _mixOrder.IndexOf(row);
            return i >= 0 ? i : -1;
        }
        return ReferenceEquals(row, _selected) ? 0 : -1;
    }

    /// <summary>重算每行的「参与演奏 / 声部序号」，并按序号写名称列文字颜色。</summary>
    private void UpdateVoiceRoles()
    {
        bool mixing = _mixOrder.Any(r => r.IsMix);
        foreach (var row in _tracks)
            row.IsVoiceActive = mixing ? row.IsMix : ReferenceEquals(row, _selected);

        ApplyVoiceBrushes();

        // 卷帘按「音高 → 声部序号」上色，与左侧列表同一个序号
        Roll.SetVoiceColors(BuildVoiceMap(GetActiveRawNotes()));
    }

    private void ApplyVoiceBrushes()
    {
        foreach (var row in _tracks)
        {
            row.VoiceIndex = VoiceIndexOf(row);
            IBrush brush;
            double opacity = 1.0;
            if (row.IsPercussion)
            {
                brush = ResourceBrush("BrushTextMuted");
                opacity = 0.55;                       // 打击乐轨固定灰
            }
            else if (row.IsVoiceActive)
            {
                brush = ResourceBrush("BrushVoice" + Music.Mod(row.VoiceIndex, PianoRoll.VoiceCount));
            }
            else
            {
                brush = ResourceBrush("BrushVoice" + Music.Mod(row.VoiceIndex, PianoRoll.VoiceCount));
                opacity = 0.45;                       // 未参与合奏：降低不透明度
            }
            if (brush is SolidColorBrush scb)
                brush = new SolidColorBrush(scb.Color, opacity);
            row.VoiceBrush = brush;
        }
    }

    /// <summary>按资源名取主题画刷。取不到就用中性文字色，绝不写字面色值。</summary>
    private static IBrush ResourceBrush(string key)
    {
        if (Application.Current?.TryFindResource(key, out var found) == true && found is IBrush b)
            return b;
        return NeutralBrush;
    }

    /// <summary>
    /// 音高 → 声部序号。同一音高归序号最小的声部（与合奏时「让位给编号小的声部」一致）。
    /// 单音线（和弦开关关闭）只保留一条线，序号一律 0。
    /// </summary>
    private Dictionary<int, int> BuildVoiceMap(IReadOnlyList<RawNote> kept)
    {
        var map = new Dictionary<int, int>();
        if (kept.Count == 0) return map;

        var keep = new HashSet<RawNote>(kept);
        var rows = ActiveRows();
        if (rows.Count == 0) return map;

        if (rows.Count == 1)
        {
            foreach (var n in rows[0].Candidate.Notes)
                if (keep.Contains(n) && !map.ContainsKey(n.Pitch)) map[n.Pitch] = 0;
            return map;
        }

        if (!_chordOn)
        {
            // 提取后的单音线：整条都算 0 号色
            foreach (var n in kept) map[n.Pitch] = 0;
            return map;
        }

        for (int i = 0; i < rows.Count; i++)
        {
            foreach (var n in rows[i].Candidate.Notes)
            {
                if (!keep.Contains(n)) continue;
                if (map.TryGetValue(n.Pitch, out int old) && old <= i) continue;
                map[n.Pitch] = i;
            }
        }
        return map;
    }

    /// <summary>当前谱面：手动编辑过就用编辑结果，否则用自动提取结果。</summary>
    private List<RawNote> GetActiveRawNotes() =>
        _editing ? _editor.Notes.ToList() : ComputeAutoNotes();

    /// <summary>
    /// 当前要演奏的音符（未移调）：把勾选的声部按优先级合并成一条单音线。
    /// 「自动提取主旋律 / 人声旋律提取」已移除 —— 它们是黑盒猜测，猜错时用户无从下手；
    /// 现在卷帘可以直接看、直接改，比猜得准。
    ///
    /// 「保留和弦」不勾选时：先按合奏顺序合并多轨，再交给 MelodyExtractor 抽一条平滑 skyline 单音线，
    /// 卷帘只显示留下的音（未保留的音不在返回值里，自然不显示）。
    /// 固定顺序：原始音 → 提取 → 移调 → NoteMapper.Map。
    /// </summary>
    private List<RawNote> ComputeAutoNotes()
    {
        var rows = ActiveRows();
        if (rows.Count == 0)
        {
            _removedLeadSec = 0;
            return new List<RawNote>();
        }

        List<RawNote> merged;
        if (_chordOn)
        {
            var voices = new List<(int Rank, RawNote Note)>();
            for (int k = 0; k < rows.Count; k++)
                foreach (var n in rows[k].Candidate.Notes) voices.Add((k + 1, n));  // 1 最优先
            merged = NoteMapper.MergeVoicesByPriority(voices);
        }
        else
        {
            // 先按合奏顺序合并多轨，再抽一条平滑 skyline 单音线；打击乐按通道与轨道名一起排除
            var voices = new List<(int Rank, string TrackName, RawNote Note)>();
            for (int k = 0; k < rows.Count; k++)
                foreach (var n in rows[k].Candidate.Notes) voices.Add((k + 1, rows[k].Name, n));
            if (voices.Count == 0) return new List<RawNote>();
            merged = PlaybackEngine.ResolveNotes(voices, chordMode: false);
        }

        // 「去除开头空拍」：整条旋律平移到第一个音从 0 秒开始（开头常有休止）
        if (ChkTrimLead.IsChecked == true)
        {
            double before = merged.Count == 0 ? 0 : merged.Min(n => n.Start);
            merged = NoteMapper.TrimLeadingSilence(merged);
            double after = merged.Count == 0 ? 0 : merged.Min(n => n.Start);
            _removedLeadSec = Math.Max(0, before - after);
        }
        else
        {
            _removedLeadSec = 0;
        }
        return merged;
    }

    /// <summary>和弦开关变化：写回设置，再刷新谱面与卷帘。</summary>
    private void ChordMode_Changed(object? sender, RoutedEventArgs e)
    {
        _chordOn = ChkChordMode.IsChecked == true;
        _cfg.ChordMode = _chordOn;
        ScheduleSave();
        if (_busy || _previewDeb is null) return;
        _previewDeb.Stop();
        DropEditsIfAny("改了和弦开关");
        RefreshPreview();
        InsertLog(_chordOn
            ? "保留和弦：多声部按优先级合成，听感更饱满。"
            : "已关闭和弦：只保留一条单音线，卷帘只显示被保留的音。");
    }

    private MappingResult MapAt(int transpose) =>
        NoteMapper.Map(GetActiveRawNotes(), transpose, manualBaseOctave: null);

    private MappingResult BuildMapping() =>
        GetActiveRawNotes().Count == 0 ? new MappingResult() : MapAt(CurrentTranspose);

    /// <summary>一键移调：找让空拍（超音域）最少的移调量。</summary>
    // ================= 导出按键表 / 宏 =================

    private void ExportGhub_Click(object? sender, RoutedEventArgs e)
        => ExportSchedule(MacroExporter.Format.LogitechGHub);

    private void ExportCsv_Click(object? sender, RoutedEventArgs e)
        => ExportSchedule(MacroExporter.Format.KeystrokeCsv);

    private async void ExportSchedule(MacroExporter.Format format)
    {
        try
        {
            var map = BuildMapping();
            var playable = map.Notes.Where(n => n.InRange).ToList();
            if (playable.Count == 0)
            {
                InsertLog("没有可演奏的音，无法导出。请调整「移调」或换一行。");
                return;
            }

            double speed = SliderSpeed.Value / 100.0;
            var timing = InputTiming.FromIndex(TimingCombo.SelectedIndex);

            string songName = _parsed == null ? "" : Path.GetFileNameWithoutExtension(_parsed.FilePath);
            string content = MacroExporter.Build(playable, format, speed, timing, songName);

            var (nCount, nEvents, seconds) = MacroExporter.Summarize(playable, speed, timing);
            string suggested = (string.IsNullOrWhiteSpace(songName) ? "midikey" : songName) +
                               MacroExporter.Extension(format);

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出按键表",
                SuggestedFileName = suggested,
                DefaultExtension = MacroExporter.Extension(format).TrimStart('.'),
                FileTypeChoices = new List<FilePickerFileType>
                {
                    new("导出文件") { Patterns = new List<string> { "*" + MacroExporter.Extension(format) } }
                }
            });
            if (file == null) return;

            string outPath = file.TryGetLocalPath() ?? "";
            if (string.IsNullOrEmpty(outPath))
            {
                InsertLog("导出失败：拿不到目标路径。");
                return;
            }
            await File.WriteAllTextAsync(outPath, content, new UTF8Encoding(true));

            InsertLog($"已导出按键表：{playable.Count} 音 / {nEvents} 事件 / {seconds:F1}s" +
                      $"（速度 {speed * 100:F0}%、档位 {timing.Name}）");
            InsertLog($"　文件：{outPath}");
            if (format == MacroExporter.Format.LogitechGHub)
                InsertLog("　G HUB 用法：设备 → 应用程序 → 添加应用程序 → 编写脚本 → 整段粘贴保存。");
            else
                InsertLog("　提示：雷蛇 Synapse 宏是私有格式，请用其宏录制功能代替。");
        }
        catch (Exception ex)
        {
            InsertLog($"导出失败：{ex.Message}");
        }
    }

    private void BtnAutoTranspose_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy || GetActiveRawNotes().Count == 0) return;

        int best = 0;
        int bestSkip = int.MaxValue;
        for (int t = -6; t <= 6; t++)
        {
            var m = MapAt(t);
            if (m.SkipCount < bestSkip ||
                (m.SkipCount == bestSkip && Math.Abs(t) < Math.Abs(best)))
            {
                bestSkip = m.SkipCount;
                best = t;
            }
        }

        SliderTranspose.Value = best;   // 触发滑块事件：保存设置并刷新预览
        string sign = best > 0 ? "+" : "";
        if (bestSkip == 0)
            InsertLog($"一键移调：整体 {sign}{best} 半音后全部音在音域内。");
        else
            InsertLog($"一键移调：整体 {sign}{best} 半音后仍剩 {bestSkip} 个音超音域（跨度过大，仍会空拍）");
        RefreshPreview();
    }

    /// <summary>勾选“合”：勾选先后即主次（先勾=1 主）；勾完立即刷新，可直接播放。</summary>
    private void Mix_Changed(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is TrackRowVM row)
        {
            DropEditsIfAny("改了合奏声部");
            bool on = cb.IsChecked == true;
            row.IsMix = on;   // 保证模型状态一致
            if (on)
            {
                if (!_mixOrder.Contains(row)) _mixOrder.Add(row);
            }
            else
            {
                _mixOrder.Remove(row);
            }
        }
        // 兜底同步 IsMix 状态
        foreach (var r in _tracks)
            if (r.IsMix && !_mixOrder.Contains(r)) _mixOrder.Add(r);
        _mixOrder.RemoveAll(r => !r.IsMix);

        for (int i = 0; i < _mixOrder.Count; i++) _mixOrder[i].MixRank = i + 1; // 1=主，2=次，3=更次
        foreach (var r in _tracks)
            if (!_mixOrder.Contains(r)) r.MixRank = 0;

        UpdateVoiceRoles();   // 声轨颜色号跟着勾选顺序走

        if (_busy || _previewDeb is null) return;
        _previewDeb.Stop();
        RefreshPreview();   // 立即刷新，勾完即可播放
    }

    private void Option_Changed(object? sender, RoutedEventArgs e)
    {
        ScheduleSave();
        if (_busy || _previewDeb is null) return;
        _previewDeb.Stop();
        _previewDeb.Start();
    }

    // ================= 播放前自检 =================

    /// <summary>播放前自检管理员权限与前台输入法，结果显示在界面状态行（✔ / ✘），不写日志。</summary>
    private void RunPreflight()
    {
        PreflightCheck.Report report;
        try
        {
            report = PreflightCheck.Run();
        }
        catch
        {
            // 检测失败就不显示结论，避免给出误导性的 ✘
            TxtCheckAdminMark.Text = "–";
            TxtCheckAdminMark.Foreground = NeutralBrush;
            TxtCheckImeMark.Text = "–";
            TxtCheckImeMark.Foreground = NeutralBrush;
            TxtCheckHint.Text = "";
            return;
        }

        PaintCheck(report.Admin, TxtCheckAdminMark, TxtCheckAdmin);
        PaintCheck(report.Ime, TxtCheckImeMark, TxtCheckIme);

        TxtCheckHint.Text = report.HasBlocked
            ? string.Join("；", new[] { report.Admin, report.Ime }
                .Where(c => c.Blocked)
                .Select(c => c.Detail))
            : string.Join("；", new[] { report.Admin, report.Ime }
                .Where(c => !c.Passed)
                .Select(c => c.Detail));

        // 输入法状态受系统影响，读不出来时把原始证据写进日志，方便远程排查
        try
        {
            var (_, probe) = PreflightCheck.DetectImeModeDetailed();
            Persist.LogFile.Append($"[自检] 输入法状态={report.Ime.Status}；{PreflightCheck.Describe(probe)}");
        }
        catch { /* 检测失败不影响使用 */ }
    }

    /// <summary>手动刷新自检（切换输入法后点一下即可）。</summary>
    private void BtnRecheck_Click(object? sender, RoutedEventArgs e)
    {
        RunPreflight();
        InsertLog("已重新检测管理员权限与输入法。");
    }

    // 与 Styles/Theme.axaml 的语义色 token 保持一致（改配色时两处一起改）
    private static readonly Avalonia.Media.IBrush OkBrush =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1E7A3C"));   // = BrushSuccess
    private static readonly Avalonia.Media.IBrush FailBrush =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#C0392B"));   // = BrushDanger
    private static readonly Avalonia.Media.IBrush NeutralBrush =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8A93A0"));   // = BrushTextMuted

    /// <summary>通过打勾、提醒打问号、未通过打叉。</summary>
    private static void PaintCheck(PreflightCheck.Check c,
                                   Avalonia.Controls.TextBlock mark,
                                   Avalonia.Controls.TextBlock label)
    {
        mark.Text = c.Status switch
        {
            PreflightCheck.Status.Pass => "✔",
            PreflightCheck.Status.Warn => "•",
            _ => "✘"
        };
        mark.Foreground = c.Status switch
        {
            PreflightCheck.Status.Pass => OkBrush,
            PreflightCheck.Status.Warn => NeutralBrush,
            _ => FailBrush
        };
        label.Foreground = mark.Foreground;
        label.Text = $"{c.Name}：{c.Detail}";
    }

    // ================= 自动检查更新 =================

    /// <summary>后台查询 GitHub 最新 Release，有新版则顶部显示提示条；没网/被墙静默忽略。</summary>
    private async Task CheckUpdateAsync()
    {
        try
        {
            var r = await AutoUpdate.CheckAsync(_cfg?.SkippedUpdateTag);
            if (r.Error != null || !r.HasUpdate || r.Skipped) return;

            _updateUrl = r.ReleaseUrl;
            _updateTag = r.LatestTag;

            UiPost(() =>
            {
                TxtUpdate.Text = $"发现新版本 v{r.LatestTag}（当前 v{r.CurrentTag}）——" +
                                 "点此打开下载页。";
                UpdateBanner.IsVisible = true;
                InsertLog($"发现新版本：v{r.LatestTag}（当前 v{r.CurrentTag}）　下载页：{r.ReleaseUrl}");
            });
        }
        catch
        {
            // 检查更新失败不影响任何功能
        }
    }

    /// <summary>左键点提示条 = 打开下载页；右键点 = 跳过本版本（下个版本仍会提示）。</summary>
    private void UpdateBanner_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (string.IsNullOrEmpty(_updateUrl)) return;

        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            if (_cfg != null && !string.IsNullOrEmpty(_updateTag))
            {
                _cfg.SkippedUpdateTag = _updateTag;
                _cfg.Save();
                UpdateBanner.IsVisible = false;
                InsertLog($"已跳过 v{_updateTag}；下个新版本仍会提示。");
            }
            return;
        }

        AutoUpdate.OpenUrl(_updateUrl);
        InsertLog($"已打开下载页：{_updateUrl}");
    }

    /// <summary>输入兼容档位：只影响下一次开始播放时的事件时序，不需要刷新预览。</summary>
    private void Timing_Changed(object? sender, SelectionChangedEventArgs e)
    {
        ScheduleSave();
        if (!_uiReady) return;
        var t = InputTiming.FromIndex(TimingCombo.SelectedIndex);
        _livePlay.Timing = t;
        InsertLog($"输入兼容档位：{t.Name}（帧 {t.FrameMs:F0}ms、修饰键提前 {t.ModLeadMs:F0}ms、" +
                  $"重触发 {t.RetriggerMs:F0}ms）");
    }

    /// <summary>「播放后自动最小化窗口」：只影响开始播放时是否缩窗，不需要刷新预览。</summary>
    private void AutoMinimize_Changed(object? sender, RoutedEventArgs e)
    {
        ScheduleSave();
    }

    // ================= 键位方案面板 =================
    //
    // 面板不新开窗口：选方案、改主键、改三个功能键、改音域与两条策略，右边是自绘的键位预览图。
    // 所有改动先落到 _keymap，再写盘（KeymapProfile.Save → %LOCALAPPDATA%\MidiKeyPlayer\keymap.json），
    // 同时把方案名记到设置里的 KeymapName。载入/保存都走 Engine\KeymapProfile 的静态 API。

    /// <summary>初始化面板：预设列表 + 当前方案 + 数值框。构造期调用一次。</summary>
    private void InitKeymapUi()
    {
        _keymapLoading = true;
        try
        {
            var names = new List<string>();
            foreach (var p in KeymapProfile.Presets)
                if (!string.IsNullOrWhiteSpace(p.Name) && !names.Contains(p.Name)) names.Add(p.Name);
            if (names.Count == 0) names.Add(KeymapProfile.DefaultName);
            KeymapCombo.ItemsSource = names;

            OutOfRangeCombo.ItemsSource = new List<string> { "丢音（不发声）", "折八度（就近）" };
            MissingNoteCombo.ItemsSource = new List<string> { "就近吸附", "丢弃" };

            int idx = names.IndexOf(_keymap.Name);
            KeymapCombo.SelectedIndex = idx >= 0 ? idx : 0;
        }
        finally
        {
            _keymapLoading = false;
        }
        RefreshKeymapUi();
    }

    /// <summary>把当前方案铺到面板上：按键列表、三个功能键、基准音、音域、两条策略、预览图。</summary>
    private void RefreshKeymapUi()
    {
        if (KeyList == null) return;
        _keymapLoading = true;
        try
        {
            KeyList.ItemsSource = null;
            KeyList.ItemsSource = _keymap.Keys;

            BtnOctaveUp.Content = KeyDisplayName(_keymap.OctaveUp);
            BtnOctaveDown.Content = KeyDisplayName(_keymap.OctaveDown);
            BtnSharpKey.Content = KeyDisplayName(_keymap.Sharp);

            TxtBaseNote.Text = _keymap.BaseNote.ToString();
            TxtBaseNoteName.Text = Music.NoteName(Math.Clamp(_keymap.BaseNote, 0, 127));
            TxtMinNote.Text = _keymap.ResolveMinNote().ToString();
            TxtMinNoteName.Text = Music.NoteName(Math.Clamp(_keymap.ResolveMinNote(), 0, 127));
            TxtMaxNote.Text = _keymap.ResolveMaxNote().ToString();
            TxtMaxNoteName.Text = Music.NoteName(Math.Clamp(_keymap.ResolveMaxNote(), 0, 127));

            OutOfRangeCombo.SelectedIndex = _keymap.OutOfRange == OutOfRangeMode.Fold ? 1 : 0;
            MissingNoteCombo.SelectedIndex = _keymap.MissingNote == MissingNoteMode.Drop ? 1 : 0;

            int idx = (KeymapCombo.ItemsSource as List<string>)?.IndexOf(_keymap.Name) ?? -1;
            if (idx >= 0 && KeymapCombo.SelectedIndex != idx) KeymapCombo.SelectedIndex = idx;

            KeymapPreview.SetProfile(_keymap);
        }
        finally
        {
            _keymapLoading = false;
        }
    }

    /// <summary>键名在按钮上的显示：null/空 = 未设置，逗号写成全角逗号。</summary>
    private static string KeyDisplayName(string? key) =>
        string.IsNullOrEmpty(key) ? "（未设置）" : DisplayKey(key!);

    /// <summary>换内置预设。</summary>
    private void KeymapCombo_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_keymapLoading || !_uiReady) return;
        string? name = KeymapCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(name) || name == _keymap.Name) return;

        KeymapProfile? found = KeymapProfile.Presets.FirstOrDefault(p => p.Name == name);
        if (found == null) return;
        RememberCurrentProfileSettings();          // 先存旧方案的速度 / 移调 / 输入档位
        string oldName = _keymap.Name;
        _keymap = CopyOf(found);   // 不直接改预设定例：复制一份再编辑
        ApplyKeymap();
        ApplyProfileSettings(_keymap.Name);        // 再读新方案的值
        InsertLog($"已换键位方案：{oldName} → {_keymap.Name}（主键 {_keymap.Keys.Count} 个，"
                  + $"速度 {SliderSpeed.Value:0}%、移调 {SliderTranspose.Value:0}、档位 {TimingCombo.SelectedIndex}）。");
    }

    /// <summary>复制方案：不直接改预设定例，复制一份再编辑。</summary>
    private static KeymapProfile CopyOf(KeymapProfile src)
    {
        try { return src.Clone(); }
        catch { return KeymapProfile.Default; }
    }

    /// <summary>方案变了：写盘 + 通知实时演奏 + 刷新面板与卷帘。</summary>
    private void ApplyKeymap()
    {
        try { _keymap.Save(); }
        catch (Exception ex) { InsertLog($"键位方案保存失败：{ex.Message}"); }
        // 实时演奏按设计读 KeymapProfile.Current，这里只换这一个真源，不再往别处复制
        KeymapProfile.Current = _keymap;
        if (_cfg != null) _cfg.KeymapName = _keymap.Name;
        ScheduleSave();
        RefreshKeymapUi();
        RefreshPreview();
    }

    /// <summary>点「主键」按钮：进入录入状态，等用户按一个键。</summary>
    private void KeyBind_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not KeyBinding kb) return;
        BeginKeyCapture(kb);
    }

    /// <summary>点三个功能键按钮：Tag = up / down / sharp。</summary>
    private void KeyBindModifier_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control c) return;
        string tag = c.Tag as string ?? "";
        if (tag.Length == 0) return;
        BeginKeyCapture(tag);
    }

    private void BeginKeyCapture(object target)
    {
        _keyCapturing = true;
        _keyCaptureTarget = target;
        TxtKeymapHint.Text = "请按一个键来绑定（Esc 取消）";
    }

    /// <summary>
    /// 录入按键：挂在 Window 的 KeyDown 上，先于控件处理。
    /// 不在录入状态就立刻返回，不干扰原有的快捷键与输入框。
    /// </summary>
    private void OnWindowPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_keyCapturing) return;

        var key = e.Key;
        string? label = KeyLabelOf(key);
        if (label == null)
        {
            if (key == Key.Escape)
            {
                _keyCapturing = false;
                _keyCaptureTarget = null;
                TxtKeymapHint.Text = "";
                e.Handled = true;
            }
            return;   // 修饰键单独按下、或不可绑定的键：继续等
        }

        e.Handled = true;
        var target = _keyCaptureTarget;
        _keyCapturing = false;
        _keyCaptureTarget = null;
        TxtKeymapHint.Text = "";

        if (target is KeyBinding kb)
        {
            string old = kb.Key;
            kb.Key = label;
            ApplyKeymap();
            InsertLog($"键位已改：{label}（原 {KeyDisplayName(old)}）。");
            RefreshKeymapUi();
        }
        else if (target is string which)
        {
            switch (which)
            {
                case "up": _keymap.OctaveUp = label; break;
                case "down": _keymap.OctaveDown = label; break;
                case "sharp": _keymap.Sharp = label; break;
            }
            ApplyKeymap();
            InsertLog($"功能键已改：{which switch { "up" => "八度上", "down" => "八度下", _ => "升半音" }} = {label}。");
        }
    }

    /// <summary>按键 → 键名（与设计文档的键名表一致）。返回 null 表示不绑定这个键。</summary>
    private static string? KeyLabelOf(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return ((char)('A' + (key - Key.A))).ToString();
        if (key is >= Key.D0 and <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return "NumPad" + (key - Key.NumPad0);
        if (key is >= Key.F1 and <= Key.F12) return "F" + (key - Key.F1 + 1);
        return key switch
        {
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemSemicolon => ";",
            Key.OemQuestion => "/",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemQuotes => "'",
            Key.OemPipe => "\\",
            Key.OemTilde => "`",
            Key.Space => "Space",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Home => "Home",
            Key.End => "End",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            Key.Tab => "Tab",
            Key.Enter => "Enter",
            Key.Back => "Back",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",
            _ => null
        };
    }

    /// <summary>追加一个键：默认取基准音，偏移 0。</summary>
    private void KeyAdd_Click(object? sender, RoutedEventArgs e)
    {
        _keymap.Keys.Add(new KeyBinding { Key = "", Offset = NextFreeOffset() });
        ApplyKeymap();
        InsertLog("已加一键，点它的键帽再按一个键即可绑定。");
    }

    /// <summary>找一个还没被占用的半音偏移，避免新键和旧键打架。</summary>
    private int NextFreeOffset()
    {
        var used = new HashSet<int>(_keymap.Keys.Select(k => k.Offset));
        for (int i = 0; i < 12; i++) if (!used.Contains(i)) return i;
        return Math.Clamp(_keymap.Keys.Count, 0, 11);
    }

    private void KeyRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not KeyBinding kb) return;
        if (_keymap.Keys.Count <= 1)
        {
            InsertLog("至少要留一个主键。");
            return;
        }
        _keymap.Keys.Remove(kb);
        ApplyKeymap();
        InsertLog("已删除一键。");
    }

    /// <summary>基准音 / 音域文本框：按回车或失焦时解析。</summary>
    private void KeymapNumber_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyKeymapNumbers(sender as TextBox);
        e.Handled = true;
    }

    /// <summary>离开输入框也解析一次，用户不用记得按回车。</summary>
    private void KeymapNumber_LostFocus(object? sender, RoutedEventArgs e)
    {
        ApplyKeymapNumbers(sender as TextBox);
    }

    private void ApplyKeymapNumbers(TextBox? box)
    {
        if (box == null || _keymapLoading) return;
        if (!int.TryParse(box.Text, out int v))
        {
            InsertLog("请输入 0 ~ 127 之间的整数。");
            RefreshKeymapUi();
            return;
        }
        v = Math.Clamp(v, 0, 127);

        if (ReferenceEquals(box, TxtBaseNote))
        {
            _keymap.BaseNote = v;
        }
        else if (ReferenceEquals(box, TxtMinNote))
        {
            if (v >= _keymap.ResolveMaxNote())
            {
                InsertLog("音域下限必须小于上限。");
                RefreshKeymapUi();
                return;
            }
            _keymap.MinNote = v;
        }
        else if (ReferenceEquals(box, TxtMaxNote))
        {
            if (v <= _keymap.ResolveMinNote())
            {
                InsertLog("音域上限必须大于下限。");
                RefreshKeymapUi();
                return;
            }
            _keymap.MaxNote = v;
        }
        else
        {
            return;
        }
        ApplyKeymap();
    }

    /// <summary>超界策略 / 缺音策略下拉。</summary>
    private void KeymapPolicy_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_keymapLoading || !_uiReady) return;
        _keymap.OutOfRange = OutOfRangeCombo.SelectedIndex == 1 ? OutOfRangeMode.Fold : OutOfRangeMode.Drop;
        _keymap.MissingNote = MissingNoteCombo.SelectedIndex == 1 ? MissingNoteMode.Drop : MissingNoteMode.Snap;
        ApplyKeymap();
    }

    /// <summary>导出当前方案为 JSON 文件。</summary>
    private async void KeymapExport_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            string safe = string.IsNullOrWhiteSpace(_keymap.Name) ? "keymap" : _keymap.Name;
            foreach (char bad in System.IO.Path.GetInvalidFileNameChars()) safe = safe.Replace(bad, '_');
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出键位方案",
                SuggestedFileName = safe + ".json",
                DefaultExtension = "json",
                FileTypeChoices = new List<FilePickerFileType>
                {
                    new("键位方案") { Patterns = new List<string> { "*.json" } }
                }
            });
            if (file == null) return;
            string? path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
            {
                InsertLog("导出键位方案失败：拿不到目标路径。");
                return;
            }
            if (!_keymap.TryExportFile(path, out string error))
            {
                InsertLog($"导出键位方案失败：{error}");
                return;
            }
            InsertLog($"已导出键位方案：{System.IO.Path.GetFileName(path)}（主键 {_keymap.Keys.Count} 个）");
        }
        catch (Exception ex)
        {
            InsertLog($"导出键位方案失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>导入方案 JSON。格式不对就弹中文提示，绝不崩。</summary>
    private async void KeymapImport_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "导入键位方案",
                AllowMultiple = false,
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new("键位方案") { Patterns = new List<string> { "*.json" } },
                    new("所有文件") { Patterns = new List<string> { "*.*" } }
                }
            });
            if (files.Count == 0) return;
            string? path = files[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
            {
                InsertLog("导入键位方案失败：拿不到文件路径。");
                return;
            }

            // 解析与校验都由键位引擎负责，error 里已经是中文原因；失败时不动原有设置
            if (!KeymapProfile.TryImportFile(path, out KeymapProfile parsed, out string why))
            {
                InsertLog($"导入失败：{why} 请选择本程序「导出方案」生成的文件。");
                return;
            }

            // 导入后方案名可能变了：同样先存旧方案，再读新方案的速度 / 移调 / 输入档位
            RememberCurrentProfileSettings();
            string oldName = _keymap.Name;
            _keymap = parsed;
            ApplyKeymap();
            ApplyProfileSettings(_keymap.Name);
            InsertLog($"已导入键位方案：{_keymap.Name}（主键 {_keymap.Keys.Count} 个，"
                      + $"音域 {Music.NoteName(_keymap.ResolveMinNote())} ~ {Music.NoteName(_keymap.ResolveMaxNote())}）。");
            if (!string.Equals(oldName, _keymap.Name, StringComparison.Ordinal))
                InsertLog($"方案名已变：{oldName} → {_keymap.Name}，速度 / 移调 / 档位已按新方案恢复。");
        }
        catch (Exception ex)
        {
            InsertLog($"导入键位方案失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ================= MIDI 设备接入（issue #4） =================

    /// <summary>重新扫描设备并尽量保持当前选择。热插拔后点「刷新」走这里。</summary>
    private void RefreshMidiDevices()
    {
        var names = MidiInputService.ListDevices();
        string want = MidiInputService.CurrentDeviceName;
        if (string.IsNullOrEmpty(want)) want = MidiDeviceCombo.SelectedItem as string ?? "";
        if (string.IsNullOrEmpty(want)) want = _cfg?.MidiDeviceName ?? "";

        var items = new List<string>();
        if (names.Count == 0) items.Add("（没有检测到 MIDI 设备）");
        else items.AddRange(names);

        MidiDeviceCombo.ItemsSource = items;
        int idx = items.IndexOf(want);
        MidiDeviceCombo.SelectedIndex = idx >= 0 ? idx : (names.Count > 0 ? 0 : 0);
        MidiDeviceCombo.IsEnabled = names.Count > 0;
        BtnMidiRefresh.IsEnabled = true;
        UpdateMidiStatus();
        UpdateMidiPanels();
    }

    private void MidiRefresh_Click(object? sender, RoutedEventArgs e)
    {
        bool wasLive = ChkMidiLive.IsChecked == true;
        if (wasLive) StopMidiLive();
        RefreshMidiDevices();
        if (wasLive) StartMidiLive();
        InsertLog($"[MIDI] 已重新扫描：找到 {MidiInputService.ListDevices().Count} 个输入设备。");
    }

    private void MidiDevice_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        ScheduleSave();
        if (ChkMidiLive.IsChecked != true) return;
        // 换了设备：先关旧的，再开新的
        StopMidiLive();
        StartMidiLive();
    }

    private void MidiLive_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        if (ChkMidiLive.IsChecked == true) StartMidiLive();
        else StopMidiLive();
        UpdateMidiPanels();
        ScheduleSave();
    }

    private void MidiOption_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_uiReady) return;
        _livePlay.BaseOctave = (int)Math.Round(SliderMidiOctave.Value);
        _livePlay.MinVelocity = (int)Math.Round(SliderMidiVelocity.Value);
        UpdateMidiLabels();
        ScheduleSave();
    }

    private void MidiAutoFit_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        _livePlay.AutoFit = ChkMidiAutoFit.IsChecked == true;
        ScheduleSave();
    }

    private void UpdateMidiLabels()
    {
        if (TxtMidiOctave != null)
            TxtMidiOctave.Text = Music.NoteName((_livePlay.BaseOctave + 1) * 12);
        if (TxtMidiVelocity != null)
            TxtMidiVelocity.Text = _livePlay.MinVelocity <= 1 ? "1" : _livePlay.MinVelocity.ToString();
    }

    private void UpdateMidiStatus()
    {
        if (TxtMidiStatus == null) return;
        bool on = ChkMidiLive.IsChecked == true;
        if (on && MidiInputService.IsListening)
        {
            TxtMidiStatus.Text = "实时演奏中";
            TxtMidiStatus.Foreground = OkBrush;
        }
        else if (on)
        {
            TxtMidiStatus.Text = "启动中…";
            TxtMidiStatus.Foreground = NeutralBrush;
        }
        else
        {
            TxtMidiStatus.Text = MidiInputService.IsListening ? "未启用" : "已停止";
            TxtMidiStatus.Foreground = NeutralBrush;
        }
    }

    /// <summary>
    /// 有设备才显示「设备 / 刷新 / 状态」这一组控件，没有设备就只留一行说明。
    /// 选项行只在勾了实时演奏时才出现，避免默认状态下多出两行用不到的东西。
    /// </summary>
    private void UpdateMidiPanels()
    {
        bool hasDevice = MidiInputService.ListDevices().Count > 0;
        if (PanelMidiControls != null) PanelMidiControls.IsVisible = hasDevice;
        if (TxtMidiNoDevice != null) TxtMidiNoDevice.IsVisible = !hasDevice;
        if (PanelMidiOptions != null) PanelMidiOptions.IsVisible = hasDevice && ChkMidiLive.IsChecked == true;
    }

    private void StartMidiLive()
    {
        string name = MidiDeviceCombo.SelectedItem as string ?? "";
        if (string.IsNullOrEmpty(name) || name.StartsWith('（'))
        {
            InsertLog("[MIDI] 没有可用设备。接上设备后点「刷新」。");
            ChkMidiLive.IsChecked = false;
            UpdateMidiStatus();
            return;
        }

        _livePlay.Timing = InputTiming.FromIndex(TimingCombo.SelectedIndex);
        _livePlay.BaseOctave = (int)Math.Round(SliderMidiOctave.Value);
        _livePlay.MinVelocity = (int)Math.Round(SliderMidiVelocity.Value);
        _livePlay.AutoFit = ChkMidiAutoFit.IsChecked == true;
        _livePlay.Start();
        UpdateMidiStatus();

        if (_midiStarting) return;
        _midiStarting = true;
        _ = MidiInputService.StartAsync(name).ContinueWith(t =>
        {
            bool ok = t.IsCompletedSuccessfully && t.Result;
            UiPost(() =>
            {
                _midiStarting = false;
                if (ok)
                {
                    InsertLog($"[MIDI] 已接入设备「{name}」。按键会直接发送到目标程序，音符范围 "
                              + $"{Music.NoteName(LivePlayback.PlayableRange(_livePlay.BaseOctave).Lo)} ~ "
                              + $"{Music.NoteName(LivePlayback.PlayableRange(_livePlay.BaseOctave).Hi)}。");
                }
                else
                {
                    InsertLog($"[MIDI] 打开设备「{name}」失败，实时演奏已关闭。");
                    ChkMidiLive.IsChecked = false;
                    _livePlay.Stop();
                }
                UpdateMidiStatus();
            });
        }, TaskScheduler.Default);
    }

    private void StopMidiLive()
    {
        bool wasListening = MidiInputService.IsListening;
        MidiInputService.Stop();
        _livePlay.Stop();
        UpdateMidiStatus();
        UpdateMidiPanels();
        if (!wasListening) return;
        InsertLog($"[MIDI] 实时演奏已停止（本次收到 {_livePlay.NoteOnCount} 个音，"
                  + $"超音域 {_livePlay.OutOfRangeCount}，顶音 {_livePlay.StolenCount}，"
                  + $"太短补足 {_livePlay.TooShortCount}）。");
    }

    /// <summary>设备事件在设备线程上触发：只做转发，界面更新交给 <see cref="OnMidiObserved"/>。</summary>
    private void OnMidiNote(int pitch, int velocity, bool down)
    {
        if (down) _livePlay.NoteOn(pitch, velocity);
        else _livePlay.NoteOff(pitch);
    }

    /// <summary>实时演奏时把最近一个音显示在状态行上，方便确认设备真的通了。</summary>
    private void OnMidiObserved(LiveMapping map, int pitch, int velocity)
    {
        if (ChkMidiLive.IsChecked != true) return;
        string text = map.Playable
            ? $"[MIDI] {Music.NoteName(pitch)} → {KeyLabelOf(map)}"
            : $"[MIDI] {Music.NoteName(pitch)} → 超出音域";
        UiPost(() => UpdateMidiStatus(text));
    }

    private void UpdateMidiStatus(string note)
    {
        if (TxtMidiStatus == null) return;
        TxtMidiStatus.Text = MidiInputService.IsListening ? note : "未启用";
        TxtMidiStatus.Foreground = MidiInputService.IsListening ? OkBrush : NeutralBrush;
    }

    /// <summary>实时演奏的提示文字：按当前键位方案的键名显示，鼠标键用中文。</summary>
    private static string KeyLabelOf(LiveMapping map)
    {
        string key = map.KeyLabel;
        if (string.IsNullOrEmpty(key)) key = "（无按键）";
        var parts = new List<string> { key };
        if (map.Low) parts.Add("+降八度键");
        if (map.High) parts.Add("+升八度键");
        if (map.Sharp) parts.Add("+升半音键");
        return string.Join("", parts);
    }

    /// <summary>刷新需选中声轨才能用的按钮（一键移调、导出），播放中也能导出。</summary>
    private void UpdateActionButtons()
    {
        bool hasRows = ActiveRows().Count > 0;
        BtnAutoTranspose.IsEnabled = hasRows;
        BtnExport.IsEnabled = hasRows;
    }

    /// <summary>刷新旋律预览与提示文案（载入文件、切换声轨、改选项后调用）。</summary>
    /// <param name="pushToRoll">
    /// true = 把谱面推回卷帘（换歌/换轨/改选项，会重置它的视口与选择）；
    /// false = 卷帘自己刚提交的编辑，数据已在它手里，不要再推回去。
    /// </param>
    /// <param name="keepView">
    /// 推回谱面时是否保留卷帘当前的缩放与位置。撤销/重做必须为 true ——
    /// 否则用户放大到某一段改谱，一按 Ctrl+Z 就被弹回全曲，没法连续编辑。
    /// </param>
    private void RefreshPreview(bool pushToRoll = true, bool keepView = false)
    {
        UpdateActionButtons();

        // 空状态引导：没有轨道时显示提示，别留一大片空白
        if (EmptyHint != null) EmptyHint.IsVisible = _tracks.Count == 0;

        var okColor = OkBrush;
        var warnColor = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#B25A00"));

        var rows = ActiveRows();
        if (rows.Count == 0 || _parsed == null)
        {
            LblMelody.Text = _parsed == null
                ? "打开 MIDI 文件并单击一行作为主旋律（可勾选“合”多声部一起演奏）。"
                : "已载入 —— 单击一行作为主旋律；勾选“合”可按 1、2、3 优先级合奏。";
            LblWarn.Text = "";
            LblWarn.Foreground = warnColor;
            _previewNotes = new List<MappedNote>();
            _previewSeconds = 0;

            // 只有"根本没载入文件"才清空卷帘。
            // 已载入但一个声部都没勾选时不能清：演奏引擎在开始播放时就把音符复制走了，
            // 清空会让左键栏掉回兜底音域（正好是 C4–C5），并让正在响的音符全部消失 ——
            // 表现就是"能出声但看不到音符"。保留卷帘内容，让画面对得上耳朵。
            if (_parsed == null) ResetSeekUi();
            else InsertLog("当前没有任何声部被勾选，卷帘保留上一次的内容。勾选一行即可恢复。");

            UpdateEditUi();
            UpdateTransportUi();
            return;
        }

        var raw = GetActiveRawNotes();
        var m = raw.Count == 0 ? new MappingResult() : NoteMapper.Map(raw, CurrentTranspose, null);
        // 声轨颜色：左侧列表的文字色与卷帘音符色用同一个序号
        var voiceMap = BuildVoiceMap(raw);
        ApplyVoiceBrushes();

        // 载入后即可定位：进度条与卷帘按谱面时间摆好（不必先播放）
        _previewNotes = m.Notes;
        double totalSec = PreviewTotalSeconds;
        _previewSeconds = Math.Clamp(_previewSeconds, 0, totalSec);
        // 卷帘轴上留 2% 余量，末尾才好双击加音
        _noteCount = raw.Count;
        // 绿=可演奏、灰=超出音域，由这份音高集合决定。
        // 两条分支都必须更新它：编辑路径不经过 SetNotes，否则改完音高颜色会按旧集合算。
        var inRangePitches = m.Notes.Where(n => n.InRange).Select(n => n.Pitch).Distinct().ToList();
        if (pushToRoll)
        {
            Roll.SetTempo(_parsed.SecondsPerBeat, _parsed.BeatsPerBar);
            Roll.DefaultNoteSeconds = MedianNoteLength();
            Roll.SetVoiceColors(voiceMap);
            Roll.SetNotes(raw, inRangePitches, totalSec * 1.02 + 0.3, preserveView: keepView);
            Roll.SetTrimInfo(_removedLeadSec);
            Roll.SetPosition(_previewSeconds);
            OnRollViewChanged();
        }
        else
        {
            // 编辑提交：只同步进度条、音高颜色与读数，卷帘保持自己的视口与选择
            Roll.SetInRangePitches(inRangePitches);
            Roll.SetVoiceColors(voiceMap);
            SliderProgress.IsEnabled = m.Notes.Count > 0;
            UpdateSeekNote();
        }
        SliderProgress.Maximum = Math.Max(0.1, totalSec);
        SliderProgress.Value = _previewSeconds;
        SliderProgress.IsEnabled = m.Notes.Count > 0;
        TxtTime.Text = $"{_previewSeconds:F1} / {totalSec:F1} s";
        UpdateSeekNote();
        UpdateEditUi();

        if (rows.Count > 1)
        {
            string order = string.Join(" > ", rows.Select(r => $"{r.MixRank}「{r.Name}」"));
            LblMelody.Text = $"合奏 {rows.Count} 个声部（优先级 {order}）：冲突时先演奏编号小的";
        }
        else
        {
            var cand = rows[0].Candidate;
            LblMelody.Text = $"主旋律：轨道 {cand.TrackIndex + 1} / 声道 {cand.Channel + 1}「{cand.Name}」" +
                             $"（共 {cand.NoteCount} 音）";
        }

        if (m.InRangeCount == 0)
        {
            LblWarn.Text = m.Notes.Count == 0
                ? "该轨道/声道没有音符，请换一行。"
                : "所有音都超出音域（低音do~高高音#do），请把“移调”调到 0 附近再试。";
            LblWarn.Foreground = warnColor;
        }
        else if (m.SkipCount > 0)
        {
            LblWarn.Text = $"有 {m.SkipCount} 个音超出音域（低音do~高高音#do），将自动空拍（可用“移调”调整）。";
            LblWarn.Foreground = warnColor;
        }
        else
        {
            LblWarn.Text = "全部音都在可演奏音域内，可直接演奏。";
            LblWarn.Foreground = okColor;
        }
        UpdateTransportUi();
    }

    // ================= 播放 =================

    private void BtnPlay_Click(object? sender, RoutedEventArgs e)
    {
        // ▶ 播放 / ⏸ 暂停 二合一：播放中点它=暂停，暂停中点它=继续
        if (_engine is { IsRunning: true })
        {
            TogglePause();
            return;
        }
        RequestPlay();
    }

    private void RequestPlay()
    {
        if (_busy || ActiveRows().Count == 0) return;

        var map = BuildMapping();
        if (map.InRangeCount == 0)
        {
            InsertLog("没有可演奏的音，无法播放。请调整“移调”或换一行。");
            return;
        }
        _playNotes = map.Notes.Where(n => n.InRange).ToList();

        SetBusy(true);
        int cd = SelectedCountdownSeconds;
        if (cd > 0)
        {
            _countdownLeft = cd;
            UpdateCountdownText();
            InsertLog($"{cd} 秒后开始——请切到目标窗口并装备乐器…");
            _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _countdownTimer.Tick += (_, _) =>
            {
                _countdownLeft--;
                if (_countdownLeft <= 0)
                {
                    _countdownTimer!.Stop();
                    _countdownTimer = null;
                    StartPlayback();
                }
                else
                {
                    UpdateCountdownText();
                }
            };
            _countdownTimer.Start();
        }
        else
        {
            StartPlayback();
        }
    }

    private int SelectedCountdownSeconds
    {
        get
        {
            int i = CountdownCombo.SelectedIndex;
            if (i < 0 || i >= CountdownOptions.Length) return 3;
            return CountdownOptions[i];
        }
    }

    private void UpdateCountdownText()
    {
        LblStatus.Foreground = FailBrush;
        LblStatus.FontSize = 38;   // 切目标程序前的最后几秒必须一眼看到
        LblStatus.Text = $"{_countdownLeft} 秒后开始 —— 请切到目标程序并装备乐器（F6 可取消）";
        SetCountdownChrome(true);
    }

    /// <summary>倒计时期间窗口底色轻微变暖做提醒，结束时恢复原色。</summary>
    private void SetCountdownChrome(bool on)
    {
        Background = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.Parse(on ? "#FFF3E4D8" : "#F4F6F9"));   // 暖色提醒 / 常态底色（= BrushCanvas）
    }

    private void StartPlayback()
    {
        if (_playNotes.Count == 0) return;

        if (_removedLeadSec > 0.05)
            InsertLog($"已去除开头空拍 {_removedLeadSec:F1} 秒，旋律从第 0 秒开始。");

        var engine = new PlaybackEngine();
        engine.ChordMode = _chordOn;   // 与界面开关一致（谱面已按开关定好）
        _engine = engine;
        engine.Log += s => UiPost(() => InsertLog(s));
        engine.Finished += () => UiPost(OnEngineFinished);

        double speed = SliderSpeed.Value / 100.0;
        bool loop = ChkLoop.IsChecked == true;

        if (!Input.InputSender.IsSupported)
            InsertLog("（当前平台不支持输入模拟，仅流程演示）");

        // 播放前可能已把进度条或卷帘拖到某个位置，从那里开始
        double startFrac = SliderProgress.Maximum > 0
            ? Math.Clamp(SliderProgress.Value / SliderProgress.Maximum, 0, 1) : 0;
        _gameHwnd = IntPtr.Zero;   // 新一轮播放重新记忆目标窗口
        engine.Timing = InputTiming.FromIndex(TimingCombo.SelectedIndex);
        engine.Play(_playNotes, speed, FixedLeadMs, loop);
        SliderProgress.Maximum = Math.Max(0.1, engine.TotalSeconds);
        if (startFrac > 0.0005)
        {
            engine.SeekFraction(startFrac);
            SliderProgress.Value = startFrac * SliderProgress.Maximum;
            InsertLog($"从 {startFrac * engine.TotalSeconds:F1} 秒开始播放。");
        }
        // 自检把“按键发不进目标程序”的常见原因指出来，省得用户逐个猜
        RunPreflight();

        string fgTitle = InputSender.ForegroundWindowTitle;
        InsertLog($"开始演奏；前台窗口：{(string.IsNullOrEmpty(fgTitle) ? "（读不到，可能未切到目标程序）" : fgTitle)}");
        LblStatus.Foreground = OkBrush;
        LblStatus.FontSize = 22;
        SetCountdownChrome(false);
        LblStatus.Text = "演奏中…";

        // 是否自动最小化由选项决定（副屏看进度时可保持窗口）
        if (ChkAutoMinimize.IsChecked == true)
        {
            if (WindowState != WindowState.Minimized)
                WindowState = WindowState.Minimized;
        }
        else
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;   // 关掉该选项时，若本来缩着就恢复出来
            InsertLog("（未自动最小化：请点一下目标窗口让它获得焦点，否则按键发到本窗口）");
        }

        UpdateTransportUi();
        SliderProgress.IsEnabled = true;

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _uiTimer.Tick += (_, _) =>
        {
            var eng = _engine;
            if (eng == null || !eng.IsRunning) return;

            // 记住“不是本程序”的前台窗口（目标程序），供停止时归还焦点用
            IntPtr fg = Input.InputSender.ForegroundWindow;
            if (fg != IntPtr.Zero && fg != SelfHwnd) _gameHwnd = fg;

            if (!_seeking)   // 拖动进度条或卷帘时不要覆盖用户位置
            {
                SliderProgress.Value = Math.Min(eng.ElapsedSeconds, SliderProgress.Maximum);
                TxtTime.Text = eng.LoopCount > 0
                    ? $"{eng.ElapsedSeconds:F1} / {eng.TotalSeconds:F1} s（第 {eng.LoopCount + 1} 遍）"
                    : $"{eng.ElapsedSeconds:F1} / {eng.TotalSeconds:F1} s";
                Roll.SetPosition(eng.ElapsedSeconds);
                UpdateSeekNote();
            }
            if (!string.IsNullOrEmpty(eng.CurrentNote))
            {
                LblStatus.Foreground = OkBrush;
                LblStatus.Text = eng.CurrentNote;
            }
        };
        _uiTimer.Start();
    }

    private void OnEngineFinished()
    {
        var eng = _engine;
        _engine = null;
        if (eng != null)
        {
            _uiTimer?.Stop();
            _uiTimer = null;
            InsertLog("播放结束。");
            InsertLog(eng.Probe.Summary());
        }
        ResetUi();
    }

    private void BtnStop_Click(object? sender, RoutedEventArgs e) => StopPlaybackNow();

    private void StopPlaybackNow()
    {
        _countdownTimer?.Stop();
        _countdownTimer = null;

        var eng = _engine;
        _engine = null;
        eng?.Stop();
        StopPreviewAudio();

        _uiTimer?.Stop();
        _uiTimer = null;
        if (eng != null) InsertLog(eng.Probe.Summary());
        InsertLog("已停止。");
        ForceReleaseKeysForGame();
        ResetUi();
    }

    private void Disclaimer_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is Border b) b.IsVisible = false;
    }

    private void ResetUi()
    {
        _liveTimer?.Stop();
        _liveTimer = null;
        _liveQueued = false;
        _seeking = false;
        SetBusy(false);
        // 停止后按谱面时间换算当前位置，方便直接重新定位
        double frac = SliderProgress.Maximum > 0 ? SliderProgress.Value / SliderProgress.Maximum : 0;
        _previewSeconds = frac * PreviewTotalSeconds;
        RefreshPreview();
        Roll.SetPosition(_previewSeconds);
        LblStatus.FontSize = 22;
        SetCountdownChrome(false);
        SetIdleHint();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        // 暂停中允许打开新 MIDI（载入时会自动先停止当前播放）
        BtnOpen.IsEnabled = !busy || (_engine is { IsRunning: true, IsPaused: true });
        TrackList.IsEnabled = !busy;
        ChkLoop.IsEnabled = !busy;
        ChkTrimLead.IsEnabled = !busy;
        ChkAutoMinimize.IsEnabled = !busy;
        BtnPreview.IsEnabled = !busy;
        CountdownCombo.IsEnabled = !busy;
        // 键位方案在演奏中不换：一轮演奏的按键表在开始时就已经算好
        ChkChordMode.IsEnabled = !busy;
        KeymapCombo.IsEnabled = !busy;
        if (BtnKeymapImport != null) BtnKeymapImport.IsEnabled = !busy;
        if (BtnKeymapExport != null) BtnKeymapExport.IsEnabled = !busy;
        if (KeyList != null) KeyList.IsEnabled = !busy;
        // 一键移调 / 导出的可用性统一由 UpdateActionButtons() 决定，这里不再覆盖。
        UpdateActionButtons();

        UpdateTransportUi();
        // 速度 / 移调两个滑条：空闲与播放中都可调（播放中实时生效）
        SliderSpeed.IsEnabled = true;
        SliderTranspose.IsEnabled = true;
    }

    // ================= 托盘 =================

    private void SetupTray()
    {
        if (!OperatingSystem.IsWindows() || _quitNow) return;
        if (_tray != null)
        {
            try { _tray.IsVisible = true; } catch { }
            return;
        }
        try
        {
            // 图标必须走内置资源：单文件发布时旁边没有 Assets 目录
            WindowIcon? icon = null;
            try
            {
                using var s = Avalonia.Platform.AssetLoader.Open(
                    new Uri("avares://MidiKeyPlayer/Assets/app.ico"));
                icon = new WindowIcon(s);
            }
            catch
            {
                // 内置资源缺失时尝试工作目录旁的 Assets/app.ico
                string p = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
                if (File.Exists(p)) icon = new WindowIcon(File.OpenRead(p));
            }

            var menu = new NativeMenu();
            var miShow = new NativeMenuItem { Header = "显示主窗口" };
            miShow.Click += (_, _) => ShowMainWindow();
            var miToggle = new NativeMenuItem { Header = "开始 / 暂停 / 继续（F6）" };
            miToggle.Click += (_, _) => ToggleControl();
            var miStop = new NativeMenuItem { Header = "停止" };
            miStop.Click += (_, _) => StopPlaybackNow();
            var miQuit = new NativeMenuItem { Header = "退出" };
            miQuit.Click += (_, _) => QuitApp();
            menu.Items.Add(miShow);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(miToggle);
            menu.Items.Add(miStop);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(miQuit);

            _tray = new TrayIcon
            {
                ToolTipText = "MIDI 按键播放器",
                Icon = icon,
                Menu = menu,
                IsVisible = true
            };
            InsertLog("托盘图标已创建（如未显示，看任务栏通知区“上箭头”内）");
        }
        catch (Exception ex)
        {
            _tray = null;
            InsertLog($"托盘初始化失败：{ex.Message}（稍后自动重试）");
        }
    }

    /// <summary>窗口显示后再确认托盘图标，Windows 偶发注册慢则延迟重试一次。</summary>
    private void EnsureTray()
    {
        SetupTray();
        if (_tray != null && _tray.IsVisible) return;
        var retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        retry.Tick += (_, _) =>
        {
            retry.Stop();
            SetupTray();
        };
        retry.Start();
    }

    private void ShowMainWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void QuitApp()
    {
        _quitNow = true;
        Close();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        SaveSettings();

        // 点 × = 彻底退出（托盘图标一并移除）
        _countdownTimer?.Stop();
        _liveTimer?.Stop();
        _uiTimer?.Stop();
        _engine?.Stop();
        StopPreviewAudio();
        _preview?.Dispose();
        _preview = null;
        Input.GlobalHotkeys.Stop();
        MidiInputService.Stop();
        _livePlay.Dispose();
        _tray?.Dispose();
        _tray = null;
        ForceReleaseKeysForGame();
    }

    /// <summary>彻底松开按键/鼠标键：停止时焦点在本窗口，先把焦点还给记住的目标窗口再补发一次。</summary>
    private void ForceReleaseKeysForGame()
    {
        try
        {
            Input.InputSender.ReleaseEverything();
            IntPtr game = _gameHwnd;
            if (game != IntPtr.Zero && game != SelfHwnd)
            {
                Input.InputSender.BringToForeground(game);
                Thread.Sleep(60);
                Input.InputSender.ReleaseEverything();
            }
        }
        catch
        {
            // 释放失败不阻断流程
        }
    }

    private IntPtr SelfHwnd
    {
        get
        {
            var ph = TryGetPlatformHandle();
            return ph?.Handle ?? IntPtr.Zero;
        }
    }
}
