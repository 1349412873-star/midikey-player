using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MidiKeyPlayer.Engine;
using MidiKeyPlayer.Midi;
using MidiKeyPlayer.Persist;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
// Avalonia.Input 也有 KeyBinding（手势绑定），这里给键位方案的绑定起个别名，避免歧义
using KeyBinding = MidiKeyPlayer.Engine.KeyBinding;

namespace MidiKeyPlayer;

/// <summary>
/// 「键位设置」窗口。整窗只有四块：方案行、按键绑定、功能键、音域与取舍。
/// 没有琴盘、没有预览图、没有图例。
///
/// 改动立即生效：写盘（<see cref="KeymapProfile.Save"/>）、刷新 <see cref="KeymapProfile.Current"/>、
/// 再通过 <see cref="ApplyPath"/> 交回主窗（主窗自己决定记日志与刷卷帘）。
/// 键位录入只挂窗口自己的 KeyDown 与 PointerPressed：窗口一关，等待态随窗口一起消失。
/// </summary>
public sealed partial class KeymapWindow : Window, INotifyPropertyChanged
{
    /// <summary>把改动交回主窗：saved 表示刚保存的方案，message 是要记进主窗日志的中文说明。</summary>
    private readonly Action<KeymapProfile?, string> _applyPath;
    /// <summary>换方案前：先把当前速度 / 移调 / 输入档位写回旧方案名。</summary>
    private readonly Action _rememberSettings;
    /// <summary>换方案后：读新方案的速度 / 移调 / 输入档位并应用，返回展示用的说明；null = 没换方案。</summary>
    private readonly Func<KeymapProfile, string?> _applyProfileSettings;

    private KeymapProfile _keymap = KeymapProfile.Default;
    private bool _loading;              // 铺值中，忽略 *Changed
    private bool _rebuilding;           // 重建列表行中

    private readonly ObservableCollection<RowVM> _rows = new();
    /// <summary>方块区的内容：已绑定的行（按音高从低到高）+ 最后那个「+ 加一条」方块。</summary>
    private readonly ObservableCollection<RowVM> _blocks = new();
    private readonly RowVM _addBlock = new() { IsAddBlock = true };
    private readonly ObservableCollection<FuncRowVM> _funcs = new();
    private RowVM? _row;                // 正在等待按键的主键行
    private FuncRowVM? _func;           // 正在等待按键的功能键行
    private bool _pendingNewRow;        // 等待中的是一条还没落地的「加一条」

    /// <summary>单独按一下修饰键再松开就算绑上，这个窗口的时长是 400 毫秒。</summary>
    private const int ModifierTapMs = 400;
    private readonly DispatcherTimer _modTimer;
    private string _modCandidate = "";  // 已经按下、还没松开的修饰键名（"" = 没有）

    private static readonly IReadOnlyList<string> KeyNameChoices = new List<string>
    {
        "Shift", "Ctrl", "Alt",
        "Space", "Enter", "Tab", "Backspace", "Insert", "Delete", "Home", "End", "PageUp", "PageDown",
        "↑", "↓", "←", "→",
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
        "小键盘 0", "小键盘 1", "小键盘 2", "小键盘 3", "小键盘 4",
        "小键盘 5", "小键盘 6", "小键盘 7", "小键盘 8", "小键盘 9",
        "鼠标左键", "鼠标中键", "鼠标右键",
    };

    /// <summary>无参构造只给设计器与 XAML 加载用；实际使用走带回调的那个重载。</summary>
    public KeymapWindow() : this(KeymapProfile.Current ?? KeymapProfile.Default, null, null, null)
    {
    }

    public KeymapWindow(KeymapProfile profile,
                        Action<KeymapProfile?, string>? applyPath,
                        Action? rememberSettings,
                        Func<KeymapProfile, string?>? applyProfileSettings)
    {
        _keymap = profile;
        _applyPath = applyPath ?? ((_, _) => { });
        _rememberSettings = rememberSettings ?? (() => { });
        _applyProfileSettings = applyProfileSettings ?? (_ => null);

        _modTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ModifierTapMs) };
        _modTimer.Tick += (_, _) =>
        {
            _modTimer.Stop();
            if (_modCandidate.Length > 0)
                Say($"已经按住超过 {ModifierTapMs} 毫秒。松开 {_modCandidate} 仍然绑它；按别的键就是组合键。");
        };

        InitializeComponent();
        DataContext = this;

        BindBlocks.ItemsSource = _blocks;
        FuncRows.ItemsSource = _funcs;

        KeyDown += Window_KeyDown;
        KeyUp += Window_KeyUp;
        InitUi();
    }

    // ================= 绑定给界面的属性 =================

    /// <summary>底部常驻统计行。</summary>
    public string StatsText { get; private set; } = "";

    /// <summary>列表为空时的灰字提示。</summary>
    public bool ShowEmptyHint => _rows.Count == 0;

    /// <summary>隐藏 <see cref="AvaloniaObject.PropertyChanged"/>：这里报的是窗口自己的两个绑定属性。</summary>
    public new event PropertyChangedEventHandler? PropertyChanged;

    private void Notify(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ================= 初始化 =================

    private void InitUi()
    {
        RefreshUi();
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_KEYMAP")))
            Dispatcher.UIThread.Post(ReportLayout, DispatcherPriority.Background);
        if (Environment.GetEnvironmentVariable("MIDIKEY_KEYMAP_PROBE_ROUNDTRIP") is string step
            && (step == "1" || step == "2"))
            Dispatcher.UIThread.Post(() => RoundTripProbe(step), DispatcherPriority.Background);
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MIDIKEY_KEYMAP_PROBE_MODIFIER")))
            Dispatcher.UIThread.Post(ModifierProbe);
    }

    /// <summary>
    /// 【开发用，可删】修饰键单击验证：给「升高八度」按一下 Shift 再松开，看绑成什么。
    /// 用环境变量 MIDIKEY_KEYMAP_PROBE_MODIFIER=1 打开，结果写 %TEMP%\midikey-modifier-probe.log。
    /// 走的是真的 Window_KeyDown / Window_KeyUp，不是另写一套判断。
    /// </summary>
    private void ModifierProbe()
    {
        string log = Path.Combine(Path.GetTempPath(), "midikey-modifier-probe.log");
        void W(string line)
        {
            try { File.AppendAllText(log, line + "\n"); } catch { }
        }
        try
        {
            var func = _funcs.FirstOrDefault(f => f.Tag == "up");
            if (func == null) { W("探针失败：找不到「升高八度」这一行"); return; }

            W($"== 修饰键单击探针（活动方案 = {_keymap.Name}）==");
            BeginWait(func);
            W($"1) 点「{func.Label}」键帽进等待：按钮文字 = 「{func.KeyCapText}」，等待中 = {func.Waiting}");

            var down = new KeyEventArgs { Key = Key.LeftShift, KeyModifiers = KeyModifiers.Shift };
            Window_KeyDown(this, down);
            W($"2) 按下 LeftShift：按钮文字 = 「{func.KeyCapText}」，等待中 = {func.Waiting}，"
              + $"方案 octaveUp 还是 = 「{_keymap.OctaveUp}」");

            var up = new KeyEventArgs { Key = Key.LeftShift, KeyModifiers = KeyModifiers.None };
            Window_KeyUp(this, up);
            W($"3) 松开 LeftShift：方案 octaveUp = 「{_keymap.OctaveUp}」，"
              + $"按钮文字 = 「{func.KeyCapText}」，等待中 = {func.Waiting}");

            string json = File.Exists(KeymapProfile.FilePath) ? File.ReadAllText(KeymapProfile.FilePath) : "";
            string octaveLine = json.Split('\n').FirstOrDefault(l => l.Contains("octaveUp", StringComparison.Ordinal)) ?? "(没找到)";
            W($"4) 写进 keymap.json 的那一行：{octaveLine.Trim()}");

            // 方块区两条主路径：加一条再按一个键、点音名区改音高
            int keysBefore = _keymap.Keys.Count;
            RowAdd_Click(null, new RoutedEventArgs());
            var pending = _rows.LastOrDefault();
            W($"5) 点「+ 加一条」：绑定 {keysBefore} → {_keymap.Keys.Count} 条（还没落地）；"
              + $"方块 {_blocks.Count} 个，最后一个是「{_blocks[^1].KeyCapText}」；"
              + $"新方块在第 {_blocks.IndexOf(pending!)} 位（倒数第二个 = 加一条方块之前），"
              + $"按钮文字「{pending?.KeyCapText}」，等待中 = {pending?.Waiting}");

            Window_KeyDown(this, new KeyEventArgs { Key = Key.Q });
            W($"6) 按 Q：绑定 {_keymap.Keys.Count} 条，键盘表最后一项 = {_keymap.Keys[^1]}；"
              + $"新方块落回音高序列（第 {_blocks.IndexOf(pending!)} 位），方块文字「{pending?.KeyCapText}」");

            string firstLabel = pending?.PitchLabel ?? "";
            ApplyPitch(pending!, PitchChoices().Last());
            W($"7) 把它的音高从「{firstLabel}」改成下拉最后一项「{pending?.PitchLabel}」："
              + $"方案里的偏移 = {pending?.Source?.Offset}，方块文字「{pending?.KeyCapText}」，"
              + $"按钮大字「{pending?.PitchName}」，小字「{pending?.PitchDegree}」");

            // 收尾：把探针加的这条拆掉，方案回到原样（脚本还会再还原一次 keymap.json）
            RowRemove_Click(new Button { DataContext = pending }, new RoutedEventArgs());
            W($"8) 收尾：绑定 {_keymap.Keys.Count} 条，方块 {_blocks.Count} 个");
        }
        catch (Exception ex)
        {
            W("探针失败：" + ex);
        }
    }

    /// <summary>
    /// 【开发用，可删】方案往返验证。两趟进程各跑一次：
    /// step=1 另存为一个自定义方案；step=2（新进程）看它有没有被载入、在下拉里、并能删掉。
    /// </summary>
    private void RoundTripProbe(string step)
    {
        string log = Path.Combine(Path.GetTempPath(), "midikey-scheme-probe.log");
        void W(string line)
        {
            try { File.AppendAllText(log, line + "\n"); } catch { }
        }
        const string name = "往返验证方案";
        try
        {
            string path = KeymapProfile.SchemeFilePath(name);
            W($"== 第 {step} 趟进程（启动时的活动方案 = {_keymap.Name}）==");
            W($"   下拉列表 = {string.Join(" | ", KeymapProfile.ListSchemeNames())}");

            if (step == "1")
            {
                if (!string.Equals(_keymap.Name, name, StringComparison.Ordinal))
                {
                    var copy = _keymap.Clone();
                    copy.Name = name;
                    _keymap = copy;
                    KeymapProfile.Current = _keymap;
                    _keymap.Save();
                    WriteSchemeFile(name);
                    RefreshUi();
                }
                W($"   另存为「{name}」：活动方案 = {_keymap.Name}；方案文件存在 = {File.Exists(path)}");
                W($"   下拉含它 = {KeymapProfile.ListSchemeNames().Contains(name)}");
            }
            else
            {
                bool loaded = string.Equals(_keymap.Name, name, StringComparison.Ordinal);
                W($"   重启后活动方案 = {_keymap.Name}；已按名字载入它 = {loaded}");
                W($"   下拉含它 = {KeymapProfile.ListSchemeNames().Contains(name)}"
                  + $"；方案文件存在 = {File.Exists(path)}");
                bool existed = File.Exists(path);
                if (existed) File.Delete(path);
                W($"   删除它：文件删前存在 = {existed}；删后存在 = {File.Exists(path)}"
                  + $"；下拉还含它 = {KeymapProfile.ListSchemeNames().Contains(name)}");
            }
        }
        catch (Exception ex)
        {
            W("探针失败：" + ex);
        }
    }

    /// <summary>整窗铺一次：方案行、按键方块、三个功能键、半音处理、统计行。</summary>
    private void RefreshUi()
    {
        if (BindBlocks == null) return;
        _loading = true;
        try
        {
            FillSchemeCombo();
            FillFuncRows();
            RadMissingSkip.IsChecked = _keymap.MissingNote == MissingNoteMode.Skip;
            RadMissingUp.IsChecked = _keymap.MissingNote == MissingNoteMode.Up;
            RadMissingDown.IsChecked = _keymap.MissingNote == MissingNoteMode.Down;
            RefreshMenuEnabled();
        }
        finally
        {
            _loading = false;
        }
        RebuildRows();
    }

    /// <summary>方案下拉：内置预设 + `schemes` 目录里的用户方案，由引擎统一列出。</summary>
    private void FillSchemeCombo()
    {
        var items = new List<string>(KeymapProfile.ListSchemeNames());
        if (items.Count == 0) items.Add(KeymapProfile.DefaultName);
        if (!string.IsNullOrWhiteSpace(_keymap.Name) && !items.Contains(_keymap.Name))
            items.Add(_keymap.Name);

        KeymapCombo.ItemsSource = items;
        int idx = items.IndexOf(_keymap.Name);
        KeymapCombo.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void FillFuncRows()
    {
        string?[] values = { _keymap.OctaveUp, _keymap.OctaveDown, _keymap.Sharp };
        string[] tags = { "up", "down", "sharp" };
        string[] labels = { "升高八度", "降低八度", "升半音" };

        _funcs.Clear();
        for (int i = 0; i < 3; i++)
        {
            string key = values[i] ?? "";
            _funcs.Add(new FuncRowVM
            {
                Tag = tags[i],
                Label = labels[i],
                Key = key,
                KeyText = DisplayKey(key),
                KeyChoices = KeyNameChoices,
            });
        }
    }

    /// <summary>
    /// 按键方块区：已绑定的行按音高从低到高排，没落地的「加一条」排在最后，
    /// 再往后固定是那个虚线方块。一行放不下由 WrapPanel 自动换行。
    /// </summary>
    private void RebuildRows()
    {
        _rebuilding = true;
        try
        {
            var ordered = _keymap.Keys
                .Where(k => k != null && !string.IsNullOrWhiteSpace(k.Key))
                .OrderBy(PitchOf)
                .ThenBy(AbsOffsetOf)
                .ToList();

            _rows.Clear();
            int n = 1;
            foreach (var kb in ordered)
                _rows.Add(NewRow(kb, n++));

            ApplyDuplicates();
            SyncRowPitchChoices();
            var waiting = ActiveRow();
            if (waiting != null) waiting.Waiting = true;

            SyncBlocks();
        }
        finally
        {
            _rebuilding = false;
        }
        RaiseStats();
    }

    /// <summary>
    /// 把 _rows 与「加一条」方块一起铺进方块区。
    /// 已落地的行按音高从低到高排（不能直接用 _rows 的顺序：刚落地的那条还在 _rows 末尾），
    /// 没落地的空行放最后，再往后固定是「+ 加一条」。
    /// </summary>
    private void SyncBlocks()
    {
        _blocks.Clear();
        foreach (var r in _rows.Where(r => r.Source != null)
                               .OrderBy(r => r.Pitch)
                               .ThenBy(r => Math.Abs(r.Source!.Offset)))
            _blocks.Add(r);
        foreach (var r in _rows.Where(r => r.Source == null)) _blocks.Add(r);
        _blocks.Add(_addBlock);
    }

    private RowVM NewRow(KeyBinding kb, int rowNo) => new()
    {
        Source = kb,
        RowNo = rowNo,
        Pitch = PitchOf(kb),
        PitchLabel = NoteLabel(PitchOf(kb)),
        KeyText = DisplayKey(kb.Key),
        KeyChoices = KeyNameChoices,
    };

    /// <summary>等待态没有落地时也要有内容可铺：能弹范围内的音高列表，格式形如 C4  1(do)。</summary>
    private void SyncRowPitchChoices()
    {
        var choices = PitchChoices();
        foreach (var row in _rows)
        {
            row.PitchChoices = choices;
            string label = NoteLabel(row.Pitch);
            if (choices.Contains(label)) row.PitchLabel = label;
        }
    }

    private List<string> PitchChoices()
    {
        int lo = _keymap.ResolveMinNote();
        int hi = _keymap.ResolveMaxNote();
        var list = new List<string>();
        for (int p = lo; p <= hi; p++) list.Add(NoteLabel(p));
        return list;
    }

    private int PitchOf(KeyBinding kb) => Math.Clamp(_keymap.BaseNote + kb.Offset, 0, 127);

    private static int AbsOffsetOf(KeyBinding kb) => Math.Abs(kb.Offset);

    /// <summary>刷新底部统计行与空列表提示。</summary>
    private void RaiseStats()
    {
        StatsText = BuildStats();
        Notify(nameof(StatsText));
        Notify(nameof(ShowEmptyHint));
    }

    // ================= 显示名 =================

    /// <summary>音高文字：音名 + 两空格 + 简谱数字加唱名。例如 C4  1(do)。</summary>
    private static string NoteLabel(int pitch)
        => $"{Music.NoteName(pitch)}  {Music.DegreeName(pitch)}({SyllableOf(pitch)})";

    /// <summary>音高的唱名。简谱带升号时用本位音的唱名，例如 #4 写 fa。</summary>
    private static string SyllableOf(int pitch) => Syllables[Music.Mod(pitch, 12)];

    /// <summary>十二个半音各自的唱名（升号与它的本位音同名）。</summary>
    private static readonly string[] Syllables =
        { "do", "do", "re", "re", "mi", "fa", "fa", "sol", "sol", "la", "la", "si" };

    /// <summary>键名在按钮上的显示。鼠标键写中文，逗号写全角并加小字「逗号」。</summary>
    private static string DisplayKey(string? key) => string.IsNullOrEmpty(key) ? "" : DisplayKeyText(key);

    private static string DisplayKeyText(string key) => key switch
    {
        "," => "， 逗号",
        "Escape" => "Esc",
        "Back" => "Backspace",
        "Space" => "Space",
        "PageUp" => "PageUp",
        "PageDown" => "PageDown",
        "MouseLeft" => "鼠标左键",
        "MouseRight" => "鼠标右键",
        "MouseMiddle" => "鼠标中键",
        _ => key,
    };

    private void Say(string message)
    {
        if (TxtSchemeHint != null) TxtSchemeHint.Text = message;
        _applyPath(null, message);
    }

    /// <summary>把当前方案存盘并交回主窗（主窗负责同步设置记忆与卷帘）。</summary>
    private void Apply(string message)
    {
        try { _keymap.Save(); }
        catch (Exception ex) { message += $"（方案保存失败：{ex.Message}）"; }
        KeymapProfile.Current = _keymap;
        if (TxtSchemeHint != null) TxtSchemeHint.Text = message;
        _applyPath(_keymap, message);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        EndWaiting(false);
    }

    // ================= 等待按键 =================

    private RowVM? ActiveRow() => _rows.FirstOrDefault(r => r.Waiting);
    private FuncRowVM? ActiveFuncRow() => _funcs.FirstOrDefault(f => f.Waiting);

    /// <summary>点方块下半（按键区）：进入等待，再按一个键就把这个键绑到这个音。</summary>
    private void BlockKey_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not RowVM row) return;
        BeginWait(row);
        e.Handled = true;   // 不要让这次点击冒到窗口，被当成「点别处」
    }

    /// <summary>点方块上半（音名区）：开一个音高下拉，选完立刻改音高。</summary>
    private void BlockPitch_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not RowVM row) return;
        e.Handled = true;
        EndWaiting(false);

        var list = new ListBox
        {
            ItemsSource = PitchChoices(),
            SelectedItem = row.PitchLabel,
            MinWidth = 120,
            MaxHeight = 260,
            FontSize = 12.5,
        };
        var flyout = new Flyout { Content = list, Placement = PlacementMode.Bottom };
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not string label) return;
            flyout.Hide();
            ApplyPitch(row, label);
        };
        flyout.ShowAt(c);
    }

    /// <summary>点功能键的键帽：进入等待。</summary>
    private void FuncKey_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not FuncRowVM row) return;
        BeginWait(row);
        e.Handled = true;
    }

    private void BeginWait(RowVM row)
    {
        EndWaiting(false);
        _row = row;
        _func = null;
        _pendingNewRow = false;
        row.Waiting = true;
        Say($"正在等按键：按一个键就绑到 {Music.NoteName(row.Pitch)}。按 Esc 取消。Shift、Ctrl、Alt 单独按一下再松开也行。");
    }

    private void BeginWait(FuncRowVM row)
    {
        EndWaiting(false);
        _row = null;
        _func = row;
        _pendingNewRow = false;
        row.Waiting = true;
        Say($"正在等按键：按一个键就作为「{row.Label}」。按 Esc 取消。Shift、Ctrl、Alt 单独按一下再松开也行。");
    }

    /// <summary>退出等待。commit = true 表示这一条已经落地，不用回滚。</summary>
    private void EndWaiting(bool commit)
    {
        var row = _row;
        var func = _func;
        bool pending = _pendingNewRow;
        _row = null;
        _func = null;
        _pendingNewRow = false;
        CancelModifierTap();

        if (row != null) row.Waiting = false;
        if (func != null) func.Waiting = false;
        // 「加一条」后没按任何键就点别处：这条空绑定直接撤掉
        if (!commit && pending && row is { Source: null })
        {
            _rows.Remove(row);
            SyncBlocks();
            RaiseStats();
        }
    }

    // ================= 单独按修饰键 =================

    /// <summary>
    /// 修饰键的键名。认不出返回 null。Windows 上按 Alt 会走 <see cref="Key.System"/>，
    /// 这时靠按住的修饰键来认。
    /// </summary>
    private static string? ModifierNameOf(KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift) return "Shift";
        if (e.Key is Key.LeftCtrl or Key.RightCtrl) return "Ctrl";
        if (e.Key is Key.LeftAlt or Key.RightAlt) return "Alt";
        if (e.Key == Key.System)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) return "Ctrl";
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return "Shift";
            return "Alt";
        }
        return null;
    }

    private static bool IsAnyModifierKey(Key key) =>
        key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.System;

    /// <summary>按下修饰键：先记下来，等松开。期间按了别的键就当组合键，不算绑定。</summary>
    private void ArmModifierTap(string name)
    {
        _modCandidate = name;
        _modTimer.Stop();
        _modTimer.Start();
        Say($"正在等按键：松开 {name} 就把它绑上（{ModifierTapMs} 毫秒内）。按别的键就是组合键，不会绑 {name}。");
    }

    private void CancelModifierTap()
    {
        _modCandidate = "";
        _modTimer.Stop();
    }

    /// <summary>松开修饰键：这一下如果没有夹着别的键，就把它绑上。</summary>
    private void Window_KeyUp(object? sender, KeyEventArgs e)
    {
        if (_modCandidate.Length == 0) return;
        if (ActiveRow() == null && ActiveFuncRow() == null) { CancelModifierTap(); return; }
        if (!IsAnyModifierKey(e.Key)) return;

        string name = _modCandidate;
        CancelModifierTap();
        CommitKey(name);
        e.Handled = true;
    }

    /// <summary>窗口级按键录入。只有在等待态才处理。</summary>
    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (ActiveRow() == null && ActiveFuncRow() == null) return;

        if (e.Key == Key.Escape)
        {
            EndWaiting(false);
            Say("已取消。");
            e.Handled = true;
            return;
        }

        // Shift / Ctrl / Alt：单独按一下再松开也算一个键，先记下，等松开再绑
        string? mod = ModifierNameOf(e);
        if (mod != null)
        {
            ArmModifierTap(mod);
            e.Handled = true;
            return;
        }

        // 按了别的键：刚才那个修饰键是组合键的一部分，取消掉
        CancelModifierTap();

        string? label = KeyLabelOf(e.Key);
        if (label == null) return;

        // 这个键已经被另一个功能键占用了：说清楚，不写错地方
        foreach (var f in _funcs)
        {
            if (f == _func) continue;
            if (!string.IsNullOrEmpty(f.Key) &&
                string.Equals(KeymapProfile.CanonicalKeyName(f.Key), label, StringComparison.OrdinalIgnoreCase))
            {
                Say($"这个键已经是「{f.Label}」了。请换一个键。");
                return;
            }
        }

        e.Handled = true;
        CommitKey(label);
    }

    /// <summary>窗口级指针录入：等待中按鼠标键就绑上。点在正在等待的按钮本身上不算。</summary>
    private void Window_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ActiveRow() == null && ActiveFuncRow() == null) return;

        var props = e.GetCurrentPoint(this).Properties;
        string? label = props.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed => "MouseLeft",
            PointerUpdateKind.RightButtonPressed => "MouseRight",
            PointerUpdateKind.MiddleButtonPressed => "MouseMiddle",
            _ => null,
        };
        if (label == null) return;

        // 鼠标一点，之前按下的修饰键就不算「单独按一下」了
        CancelModifierTap();

        // 点「选键名」链接、或者点已经打开的键名下拉：不算绑鼠标键
        if (IsKeyNameUx(e.Source as Visual)) return;

        // 再点一次正在等待的那个按钮 = 取消
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(true) is { DataContext: RowVM r1 } && r1.Waiting)
        {
            EndWaiting(false);
            Say("已取消。");
            return;
        }
        if (e.Source is Visual v2 && v2.FindAncestorOfType<Button>(true) is { DataContext: FuncRowVM r2 } && r2.Waiting)
        {
            EndWaiting(false);
            Say("已取消。");
            return;
        }

        CommitKey(label);
    }

    /// <summary>点在键帽按钮或键名下拉自身时，不要当成「点别处」。</summary>
    private static bool IsKeyNameUx(Visual? source)
    {
        if (source == null) return false;
        if (source.FindAncestorOfType<Button>(true) is not null) return true;
        return source.FindAncestorOfType<ComboBox>(true) is not null;
    }

    /// <summary>落下这个键：主键行写 Key，功能键行写 up / down / sharp。</summary>
    private void CommitKey(string label)
    {
        string canonical = KeymapProfile.CanonicalKeyName(label);
        if (canonical.Length == 0)
        {
            Say($"不认识的键名「{label}」，请换一个键。");
            return;
        }

        var func = _func;
        var row = _row;
        bool pending = _pendingNewRow;
        EndWaiting(true);

        if (func != null)
        {
            switch (func.Tag)
            {
                case "up": _keymap.OctaveUp = canonical; break;
                case "down": _keymap.OctaveDown = canonical; break;
                case "sharp": _keymap.Sharp = canonical; break;
            }
            func.Key = canonical;
            func.KeyText = DisplayKeyText(canonical);
            Apply($"功能键已改：「{func.Label}」= {DisplayKeyText(canonical)}");
            return;
        }

        if (row == null) return;

        // 同一个键在别的行上已经用过：按需求允许并存，只给提示，不阻止保存
        var same = _rows.Where(r => r != row && string.Equals(r.Source?.Key, canonical, StringComparison.OrdinalIgnoreCase)).ToList();

        if (row.Source == null)
        {
            var kb = new KeyBinding { Key = canonical, Offset = row.Pitch - _keymap.BaseNote };
            _keymap.Keys.Add(kb);
            row.Source = kb;
        }
        else
        {
            row.Source.Key = canonical;
        }
        row.KeyText = DisplayKeyText(canonical);
        ApplyDuplicates();
        SyncBlocks();     // 新落地的这条要按音高排回队伍里
        RaiseStats();
        Apply($"{Music.NoteName(row.Pitch)} 已绑到 {DisplayKeyText(canonical)}"
              + (same.Count > 0 ? "。这个键还绑在别的音上，两个音都会响。" : "。"));
    }

    // ================= 主键方块：音高 / 解绑 / 加一条 =================

    private void RowAdd_Click(object? sender, RoutedEventArgs e)
    {
        EndWaiting(false);
        int pitch = _keymap.ResolveMinNote();
        var row = new RowVM
        {
            RowNo = _rows.Count + 1,
            Pitch = pitch,
            PitchLabel = NoteLabel(pitch),
            KeyText = "",
            KeyChoices = KeyNameChoices,
            Source = null,
        };
        row.PitchChoices = PitchChoices();
        _rows.Add(row);
        SyncBlocks();
        RaiseStats();
        BeginWait(row);
        _pendingNewRow = true;
        Say("加了一条空白绑定：现在按一个键就绑上了。没按就点别处，这条会撤掉。");
    }

    private void RowRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not RowVM row) return;
        if (row.Source != null)
        {
            if (ReferenceEquals(_row, row)) EndWaiting(false);
            _keymap.Keys.Remove(row.Source);
            Apply($"已解绑 {Music.NoteName(row.Pitch)}。");
        }
        _rows.Remove(row);
        ApplyDuplicates();
        SyncBlocks();
        RaiseStats();
        e.Handled = true;
    }

    /// <summary>把某一行的音高改成下拉里选中的那个。音高变了顺序也要跟着重排。</summary>
    private void ApplyPitch(RowVM row, string label)
    {
        if (_loading || _rebuilding) return;

        int pitch = PitchOfLabel(label, row.Pitch);
        if (pitch == row.Pitch && row.Source != null) return;

        if (row.Source != null)
        {
            row.Source.Offset = pitch - _keymap.BaseNote;
            row.Pitch = pitch;
            Apply($"{DisplayKeyText(row.Source.Key)} 的音高已改到 {Music.NoteName(pitch)}。");
        }
        else
        {
            row.Pitch = pitch;
        }
        SyncRowPitchChoices();
        SyncBlocks();
        RaiseStats();
    }

    /// <summary>把下拉里的「C4  1(do)」换回音高数字。解析不出来就保持原值。</summary>
    private static int PitchOfLabel(string label, int fallback)
    {
        int i = label.IndexOf("  ", StringComparison.Ordinal);
        string name = i > 0 ? label[..i] : label;
        for (int p = 0; p <= 127; p++)
            if (string.Equals(Music.NoteName(p), name, StringComparison.Ordinal)) return p;
        return fallback;
    }

    // ================= 功能键 =================

    private void FuncClear_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not FuncRowVM row) return;
        ClearFuncKey(row);
        e.Handled = true;
    }

    /// <summary>把一个功能键设成「未设置」并写盘。</summary>
    private void ClearFuncKey(FuncRowVM row)
    {
        if (ReferenceEquals(_func, row)) EndWaiting(false);
        switch (row.Tag)
        {
            case "up": _keymap.OctaveUp = ""; break;
            case "down": _keymap.OctaveDown = ""; break;
            case "sharp": _keymap.Sharp = ""; break;
        }
        row.Key = "";
        row.KeyText = "";
        Apply($"「{row.Label}」已设为未设置。");
    }

    // ================= 选键名 =================

    private void KeyName_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox c || c.SelectedItem is not string label) return;
        if (_loading || _rebuilding) return;

        string canonical = InternalKeyName(label);
        if (canonical.Length == 0) return;

        // ComboBox 自身不会重复触发：选完立刻复位，下次还是「选键名」
        c.SelectedItem = null;
        CommitKey(canonical);
        e.Handled = true;
    }

    /// <summary>中文显示名换回方案里的键名。</summary>
    private static string InternalKeyName(string label) => label switch
    {
        "↑" => "Up",
        "↓" => "Down",
        "←" => "Left",
        "→" => "Right",
        "鼠标左键" => "MouseLeft",
        "鼠标中键" => "MouseMiddle",
        "鼠标右键" => "MouseRight",
        _ when label.StartsWith("小键盘 ", StringComparison.Ordinal) => "NumPad" + label[4..],
        _ => label,
    };

    // ================= 半音怎么处理 =================

    /// <summary>
    /// 三选一：缺的那个半音是跳过，还是用上/下那个音代替。
    /// 音域没有输入框，能弹范围完全由键位推导，这里只影响范围内的半音。
    /// </summary>
    private void MissingNote_Checked(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not RadioButton rb) return;

        var mode = rb.Name switch
        {
            nameof(RadMissingSkip) => MissingNoteMode.Skip,
            nameof(RadMissingDown) => MissingNoteMode.Down,
            _ => MissingNoteMode.Up,
        };
        if (_keymap.MissingNote == mode) return;
        _keymap.MissingNote = mode;
        Apply($"半音处理已改：{MissingNoteText(mode)}。");
    }

    private static string MissingNoteText(MissingNoteMode mode) => mode switch
    {
        MissingNoteMode.Skip => "跳过这个音",
        MissingNoteMode.Up => "用高半音代替",
        _ => "用低半音代替",
    };

    // ================= 方案管理 =================

    private void KeymapCombo_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (KeymapCombo.SelectedItem is not string name) return;
        if (string.Equals(name, _keymap.Name, StringComparison.Ordinal)) return;

        EndWaiting(false);
        var found = KeymapProfile.LoadByName(name);
        if (found == null)
        {
            // 文件被删掉或读不动了：把下拉拉回当前方案，别让界面停在一个不存在的方案上
            Say($"读不到方案「{name}」，已保持当前方案。");
            FillSchemeCombo();
            return;
        }

        _rememberSettings();
        string oldName = _keymap.Name;
        _keymap = found;
        KeymapProfile.Current = _keymap;
        try { _keymap.Save(); } catch { /* 保存失败由主窗日志告知 */ }
        string? extra = _applyProfileSettings(_keymap);
        RefreshUi();
        Say($"已换方案：{oldName} → {_keymap.Name}（主键 {BindCount()} 个）"
            + (string.IsNullOrEmpty(extra) ? "。" : "。" + extra));
    }

    private async void SchemeSaveAs_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            EndWaiting(false);
            string? name = await PromptName("新方案叫什么名字", "另存为新方案", _keymap.Name + " 副本");
            if (string.IsNullOrEmpty(name)) return;

            string final = SanitizeSchemeName(name);
            if (final.Length == 0)
            {
                Say("方案名不能是空的。");
                return;
            }
            if (KeymapProfile.IsBuiltInSchemeName(final))
            {
                Say($"「{final}」是内置方案名，不能覆盖。请换一个名字。");
                return;
            }
            if (KeymapProfile.SchemeNameExists(final))
            {
                Say($"已经有同名方案「{final}」，请换一个名字。");
                return;
            }

            var copy = _keymap.Clone();
            copy.Name = final;
            _keymap = copy;
            KeymapProfile.Current = _keymap;
            _keymap.Save();   // 活动方案仍然只写 keymap.json
            WriteSchemeFile(final);   // 另存的那一份写进 schemes 目录，重启后还能选到
            RefreshUi();
            Apply($"已另存为新方案「{final}」。");
        }
        catch (Exception ex)
        {
            Say($"另存方案失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async void SchemeRename_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (IsBuiltIn()) { Say("内置方案不能改，可以先另存一份。"); return; }
            EndWaiting(false);
            string? name = await PromptName("改成什么名字", "重命名方案", _keymap.Name);
            if (string.IsNullOrEmpty(name) || name == _keymap.Name) return;

            string final = SanitizeSchemeName(name);
            if (final.Length == 0) { Say("方案名不能是空的。"); return; }
            if (KeymapProfile.IsBuiltInSchemeName(final))
            {
                Say($"「{final}」是内置方案名，不能占用。请换一个名字。");
                return;
            }
            if (KeymapProfile.SchemeNameExists(final))
            {
                Say($"已经有同名方案「{final}」，请换一个名字。");
                return;
            }

            string oldName = _keymap.Name;
            string oldPath = KeymapProfile.SchemeFilePath(oldName);
            _keymap.Name = final;
            KeymapProfile.Current = _keymap;
            _keymap.Save();            // 活动方案：名字换了，keymap.json 跟着变
            WriteSchemeFile(final);    // 新名字的文件
            if (!string.Equals(oldPath, KeymapProfile.SchemeFilePath(final), StringComparison.OrdinalIgnoreCase))
                TryDeleteFile(oldPath); // 旧名字的文件删掉
            RefreshUi();
            Apply($"方案已改名：{oldName} → {final}。");
        }
        catch (Exception ex)
        {
            Say($"重命名失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async void SchemeDelete_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (IsBuiltIn()) { Say("内置方案不能改，可以先另存一份。"); return; }
            EndWaiting(false);
            string name = _keymap.Name;
            bool ok = await Confirm("删除方案", $"删除方案「{name}」？删了就找不回来了。", "删除", danger: true);
            if (!ok) return;

            string path = KeymapProfile.SchemeFilePath(name);
            bool fileExisted = false;
            try
            {
                fileExisted = File.Exists(path);
                if (fileExisted) File.Delete(path);
            }
            catch (Exception ex)
            {
                Say($"删除失败：{ex.Message}");
                return;
            }

            // 活动方案换成默认内置方案
            _keymap = KeymapProfile.PresetByName(KeymapProfile.DefaultName)?.Clone() ?? KeymapProfile.Default;
            KeymapProfile.Current = _keymap;
            _keymap.Save();
            RefreshUi();
            string tail = fileExisted ? "" : "（没有找到它的方案文件）";
            Apply($"已删除方案「{name}」{tail}。已切回默认方案。");
        }
        catch (Exception ex)
        {
            Say($"删除失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async void SchemeRestore_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            EndWaiting(false);
            bool ok = await Confirm("恢复默认设置",
                "把当前方案的键位、功能键与半音处理改回初始值？你的其他方案不受影响。", "恢复", danger: false);
            if (!ok) return;

            string keepName = _keymap.Name;
            var fresh = IsBuiltIn()
                ? KeymapProfile.PresetByName(keepName)?.Clone()
                : KeymapProfile.PresetByName(KeymapProfile.DefaultName)?.Clone();
            if (fresh == null) { Say("找不到默认键位，未做改动。"); return; }

            fresh.Name = keepName;
            _keymap = fresh;
            KeymapProfile.Current = _keymap;
            Apply($"「{keepName}」已恢复默认设置。");
            RefreshUi();
        }
        catch (Exception ex)
        {
            Say($"恢复默认失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private bool IsBuiltIn() => KeymapProfile.PresetByName(_keymap.Name) != null;

    private void RefreshMenuEnabled()
    {
        bool fixedScheme = IsBuiltIn();
        MiSchemeRename.IsEnabled = !fixedScheme;
        MiSchemeDelete.IsEnabled = !fixedScheme;
        ToolTip.SetTip(MiSchemeRename, fixedScheme ? "内置方案不能改，可以先另存一份" : "给这套方案换个名字");
        ToolTip.SetTip(MiSchemeDelete, fixedScheme ? "内置方案不能改，可以先另存一份" : "删掉这套方案");
    }

    // ================= 导入 / 导出 =================

    private async void KeymapExport_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            EndWaiting(false);
            string safe = string.IsNullOrWhiteSpace(_keymap.Name) ? "keymap" : _keymap.Name;
            foreach (char bad in Path.GetInvalidFileNameChars()) safe = safe.Replace(bad, '_');
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
            if (string.IsNullOrEmpty(path)) { Say("导出失败：拿不到目标路径。"); return; }
            if (!_keymap.TryExportFile(path, out string error)) { Say($"导出失败：{error}"); return; }
            Say($"已导出方案：{Path.GetFileName(path)}（主键 {BindCount()} 个）。");
        }
        catch (Exception ex)
        {
            Say($"导出失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async void KeymapImport_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            EndWaiting(false);
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
            if (string.IsNullOrEmpty(path)) { Say("导入失败：拿不到文件路径。"); return; }

            if (!KeymapProfile.TryImportFile(path, out var parsed, out string why))
            {
                Say($"导入失败：{why} 原来的方案没动。");
                return;
            }

            _rememberSettings();
            string oldName = _keymap.Name;
            _keymap = parsed;
            KeymapProfile.Current = _keymap;
            _keymap.Save();
            string? extra = _applyProfileSettings(_keymap);
            RefreshUi();
            string msg = $"已导入方案：{_keymap.Name}（主键 {BindCount()} 个，"
                         + $"音域 {Music.NoteName(_keymap.ResolveMinNote())} ~ {Music.NoteName(_keymap.ResolveMaxNote())}）";
            if (!string.Equals(oldName, _keymap.Name, StringComparison.Ordinal))
                msg += $"。方案名已变：{oldName} → {_keymap.Name}";
            if (!string.IsNullOrEmpty(extra)) msg += "。" + extra;
            Apply(msg);
        }
        catch (Exception ex)
        {
            Say($"导入失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ================= 方案文件 =================
    //
    // 目录与文件名的规则都在引擎里（KeymapProfile.SchemeFilePath），这里只负责读写与删。
    // 活动方案永远是 keymap.json；「另存为」「重命名」额外往 schemes 目录写一份，重启后还能选到。

    /// <summary>把方案名整理成能当文件名的样子：去掉两端空格与非法字符。</summary>
    private static string SanitizeSchemeName(string? want)
    {
        string safe = (want ?? "").Trim();
        foreach (char bad in Path.GetInvalidFileNameChars()) safe = safe.Replace(bad, '_');
        return safe.Trim();
    }

    /// <summary>把当前方案写进 schemes\&lt;名字&gt;.json。写不动就返回 false 并给出中文原因。</summary>
    private bool WriteSchemeFile(string name, out string error)
    {
        error = "";
        try
        {
            string path = KeymapProfile.SchemeFilePath(name);
            string dir = Path.GetDirectoryName(path) ?? "";
            if (dir.Length > 0) Directory.CreateDirectory(dir);
            File.WriteAllText(path, _keymap.ToJson());
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private bool WriteSchemeFile(string name)
    {
        bool ok = WriteSchemeFile(name, out string why);
        if (!ok) Say($"方案文件没写成：{why}");
        return ok;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ================= 统计 =================

    private int BindCount() =>
        _keymap.Keys.Count(k => k != null && !string.IsNullOrWhiteSpace(k.Key));

    private int MissingInRange()
    {
        int lo = _keymap.ResolveMinNote();
        int hi = _keymap.ResolveMaxNote();
        var bound = new HashSet<int>();
        foreach (var k in _keymap.Keys)
        {
            if (k == null || string.IsNullOrWhiteSpace(k.Key)) continue;
            int p = Math.Clamp(_keymap.BaseNote + k.Offset, 0, 127);
            if (p >= lo && p <= hi) bound.Add(p);
        }
        int missing = 0;
        for (int p = lo; p <= hi; p++) if (!bound.Contains(p)) missing++;
        return missing;
    }

    private string BuildStats()
    {
        int lo = _keymap.ResolveMinNote();
        int hi = _keymap.ResolveMaxNote();
        return $"绑了 {BindCount()} 条。能弹 {Music.NoteName(lo)} 到 {Music.NoteName(hi)}，"
               + $"其中 {MissingInRange()} 个音还没有绑键。";
    }

    // ================= 重复绑定提示 =================

    /// <summary>同一个物理键绑到多个音：涉及的行都显示红字，但不阻止保存。</summary>
    private void ApplyDuplicates()
    {
        var count = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _rows)
        {
            string key = r.Source?.Key ?? "";
            if (key.Length == 0) continue;
            count.TryGetValue(key, out int c);
            count[key] = c + 1;
        }
        foreach (var r in _rows)
        {
            string key = r.Source?.Key ?? "";
            r.IsDuplicate = key.Length > 0 && count.TryGetValue(key, out int c) && c > 1;
        }
    }

    // ================= 键盘录入 =================

    /// <summary>按键 → 键名。返回 null 表示不绑定这个键。</summary>
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

    // ================= 小对话框 =================

    private async Task<string?> PromptName(string title, string okText, string initial)
    {
        var box = new TextBox { Text = initial ?? "", Width = 300 };
        var ok = new Button { Content = okText, Classes = { "accent" }, Padding = new Thickness(16, 4) };
        var cancel = new Button { Content = "取消", Classes = { "secondary" }, Padding = new Thickness(16, 4) };
        var dlg = new Window
        {
            Title = title,
            Width = 350,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = this.Background,
            FontFamily = FontFamily,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap },
                    box,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancel, ok },
                    },
                },
            },
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            dlg.Close(box.Text ?? "");
            e.Handled = true;
        };
        ok.Click += (_, _) => dlg.Close(box.Text ?? "");
        cancel.Click += (_, _) => dlg.Close(null);
        var result = await dlg.ShowDialog<string?>(this);
        return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
    }

    private async Task<bool> Confirm(string title, string body, string okText, bool danger)
    {
        var ok = new Button
        {
            Content = okText,
            Classes = { "accent" },
            Padding = new Thickness(16, 4),
        };
        var cancel = new Button { Content = "取消", Classes = { "secondary" }, Padding = new Thickness(16, 4) };
        var dlg = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = this.Background,
            FontFamily = FontFamily,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, LineHeight = 20 },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancel, ok },
                    },
                },
            },
        };
        ok.Click += (_, _) => dlg.Close(true);
        cancel.Click += (_, _) => dlg.Close(false);
        return await dlg.ShowDialog<bool>(this);
    }

    // ================= 快照用诊断 =================

    /// <summary>只在拍快照时打印布局尺寸，便于核对四块是否清楚、有没有溢出。</summary>
    private void ReportLayout()
    {
        try
        {
            double contentH = this.Content is Control c ? c.Bounds.Height : 0;
            double extent = MainScroll?.Extent.Height ?? 0;
            double viewport = MainScroll?.Viewport.Height ?? 0;
            double cardSum = 0;
            if (MainScroll?.Content is Control scrollBody)
                foreach (var child in scrollBody.GetVisualChildren().OfType<Control>())
                    cardSum += child.Bounds.Height;
            string line = $"[键位窗口#{GetHashCode()}] 窗口 {Bounds.Width:F0}x{Bounds.Height:F0}；内容高 {contentH:F0}；"
                          + $"滚动区 内容 {extent:F0} / 视口 {viewport:F0}；需要滚动={extent > viewport + 0.5}；"
                          + $"四块合计 {cardSum:F0}；"
                          + $"绑定 {BindCount()} 条；方块 {_blocks.Count} 个（含「加一条」）；"
                          + $"等待行={(_row == null ? "无" : _row.PitchLabel)}"
                          + $" 等待功能键={(_func?.Label ?? "无")}；"
                          + $"半音处理={MissingNoteText(_keymap.MissingNote)}；"
                          + $"方案={_keymap.Name}；能弹 {Music.NoteName(_keymap.ResolveMinNote())}~{Music.NoteName(_keymap.ResolveMaxNote())}；"
                          + $"统计「{StatsText}」";
            Console.WriteLine(line);
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "midikey-snap.log"), line + "\n");
            }
            catch { }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("keymap layout report failed: " + ex.Message);
        }
    }
}

// ================= 视觉模型 =================

/// <summary>
/// 一行「键帽」的公共部分：主键行与功能键行共用等待态的显示规则。
/// 键帽有三种状态：有值（显示中文键名）、未设置（灰字「未设置」）、等待按键（「按一个键…」＋高亮描边）。
/// 等待态的高亮由外面一层 Border 承担：它不覆盖按钮模板，静态可见，不依赖闪烁动画。
/// </summary>
internal abstract class KeyCapRow : INotifyPropertyChanged
{
    private string _keyText = "";
    private bool _waiting;

    /// <summary>按钮上平时显示的键名。空串 = 这条还没设置。</summary>
    public string KeyText { get => _keyText; set => Set(ref _keyText, value); }

    /// <summary>是否正在等用户按一个键。</summary>
    public bool Waiting { get => _waiting; set => Set(ref _waiting, value); }

    /// <summary>没设置（方案里是空字符串）。</summary>
    public bool IsEmpty => !Waiting && _keyText.Length == 0;

    /// <summary>按钮文字：等待 → 提示语，未设置 → 灰字「未设置」，有值 → 键名。</summary>
    public string KeyCapText =>
        Waiting ? "按一个键…" :
        _keyText.Length > 0 ? _keyText :
        "未设置";

    /// <summary>未设置的键帽用次要文字色，一眼能看出「这里还没绑」。</summary>
    public IBrush? KeyCapForeground => FindBrush(IsEmpty ? "BrushTextMuted" : "BrushText");

    /// <summary>等待态描边加粗一档。</summary>
    public Thickness WaitBorderThickness => Waiting ? new Thickness(2) : new Thickness(0);

    /// <summary>等待态描边色。取现有主题资源，不自造配色。</summary>
    public IBrush? WaitBorderBrush => Waiting ? FindBrush("BrushAccent") : null;

    protected static IBrush? FindBrush(string key) =>
        Application.Current != null && Application.Current.TryFindResource(key, out object? brush) && brush is IBrush b
            ? b
            : null;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>子类改自己的字段也走这里。返回 true 表示值真的变了。</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnSet(name);
        return true;
    }

    /// <summary>字段变了之后要通知哪些属性。派生类可以补充。</summary>
    protected virtual void OnSet(string? name)
    {
        Raise(name);
        if (name == nameof(Waiting) || name == nameof(KeyText))
        {
            Raise(nameof(IsEmpty));
            Raise(nameof(KeyCapText));
            Raise(nameof(KeyCapForeground));
            Raise(nameof(WaitBorderThickness));
            Raise(nameof(WaitBorderBrush));
        }
    }

    /// <summary>事件只能从这里发：声明 PropertyChanged 的类才允许触发它。</summary>
    protected void Raise(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 一个键帽方块。已绑定的音与最后那个「+ 加一条」方块共用同一个模型：
/// <see cref="IsAddBlock"/> 为 true 的就是虚线方块。
/// </summary>
internal sealed class RowVM : KeyCapRow
{
    public KeyBinding? Source { get; set; }
    public int RowNo { get; set; }

    private int _pitch;
    /// <summary>这个键弹出的音高（MIDI 编号）。</summary>
    public int Pitch
    {
        get => _pitch;
        set
        {
            if (!Set(ref _pitch, value)) return;
            Raise(nameof(PitchName));
            Raise(nameof(PitchDegree));
        }
    }

    public IReadOnlyList<string> PitchChoices { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> KeyChoices { get; set; } = Array.Empty<string>();

    /// <summary>方块左上角的大字：音名，例如 C4。</summary>
    public string PitchName => Music.NoteName(_pitch);

    /// <summary>音名下面的小字：简谱加唱名，例如 1(do)。</summary>
    public string PitchDegree => $"{Music.DegreeName(_pitch)}({SyllableOf(_pitch)})";

    private static readonly string[] Syllables =
        { "do", "do", "re", "re", "mi", "fa", "fa", "sol", "sol", "la", "la", "si" };

    private static string SyllableOf(int pitch) => Syllables[Music.Mod(pitch, 12)];

    private string _pitchLabel = "";
    /// <summary>音高下拉里的写法，形如「C4  1(do)」。</summary>
    public string PitchLabel { get => _pitchLabel; set => Set(ref _pitchLabel, value); }

    private bool _duplicate;
    /// <summary>同一个物理键绑到了多个音：方块描红边提醒。</summary>
    public bool IsDuplicate
    {
        get => _duplicate;
        set
        {
            if (!Set(ref _duplicate, value)) return;
            Raise(nameof(BlockBorderBrush));
            Raise(nameof(BlockBorderThickness));
        }
    }

    private bool _addBlock;
    /// <summary>这个方块是「+ 加一条」。</summary>
    public bool IsAddBlock
    {
        get => _addBlock;
        set
        {
            if (!Set(ref _addBlock, value)) return;
            Raise(nameof(IsRowBlock));
        }
    }

    /// <summary>这个方块是一条绑定。</summary>
    public bool IsRowBlock => !_addBlock;

    /// <summary>方块描边：等待按键 → 品牌绿加粗；重复绑定 → 红边；平时 → 一像素灰边。</summary>
    public IBrush? BlockBorderBrush =>
        Waiting ? FindBrush("BrushAccent")
        : IsDuplicate ? FindBrush("BrushDanger")
        : FindBrush("BrushBorderStrong");

    /// <summary>方块描边粗细。等待态加粗一档。</summary>
    public Thickness BlockBorderThickness =>
        Waiting ? new Thickness(2) : IsDuplicate ? new Thickness(1.5) : new Thickness(1);

    protected override void OnSet(string? name)
    {
        base.OnSet(name);
        if (name == nameof(Waiting))
        {
            Raise(nameof(BlockBorderBrush));
            Raise(nameof(BlockBorderThickness));
        }
    }
}

/// <summary>一行功能键。</summary>
internal sealed class FuncRowVM : KeyCapRow
{
    public string Tag { get; set; } = "";
    public string Label { get; set; } = "";
    public string Key { get; set; } = "";
    public IReadOnlyList<string> KeyChoices { get; set; } = Array.Empty<string>();
}
