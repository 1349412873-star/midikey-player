using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Text;
using MidiKeyPlayer.Engine;
using MidiKeyPlayer.Midi;
using MidiKeyPlayer.Persist;
// Avalonia.Input 也有 KeyBinding（手势绑定），这里给键位方案的绑定起个别名，避免歧义
using KeyBinding = MidiKeyPlayer.Engine.KeyBinding;

namespace MidiKeyPlayer;

/// <summary>
/// 键位设置独立窗口。原来这套控件堆在主窗「选项区」里，把中间占 `*` 的钢琴卷帘压扁了；
/// 现在整块搬到这里，主窗只留一个「键位设置…」按钮。
///
/// 窗口里的改动立即生效：写盘（KeymapProfile.Save）、刷新 <see cref="KeymapProfile.Current"/>、
/// 通过 <see cref="ApplyPath"/> 交回主窗去同步设置记忆与卷帘预览。
/// 键位录入用窗口自己的 KeyDown：窗口一关，<c>_keyCapturing</c> 随窗口一起消失，不会给主窗留残留状态。
/// </summary>
public sealed partial class KeymapWindow : Window
{
    /// <summary>把改动交回主窗：saved 表示刚保存的方案，message 是要记进主窗日志的中文说明。</summary>
    private readonly Action<KeymapProfile?, string> _applyPath;
    /// <summary>换方案前：先把当前速度 / 移调 / 输入档位写回旧方案名。</summary>
    private readonly Action _rememberSettings;
    /// <summary>换方案后：读新方案的速度 / 移调 / 输入档位并应用，返回展示用的说明；null = 没换方案。</summary>
    private readonly Func<KeymapProfile, string?> _applyProfileSettings;

    private KeymapProfile _keymap = KeymapProfile.Default;
    private bool _loading;                  // 面板初始化 / 载入预设中，忽略 *Changed
    private bool _keyCapturing;             // 正在等用户按一个键来绑定
    private object? _keyCaptureTarget;      // KeyBinding 或 "octaveUp"/"octaveDown"/"sharp"

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

        InitializeComponent();

        KeyDown += OnKeyDownCapture;   // 键位录入：先于控件处理按键
        InitUi();
    }

    // ================= 初始化与铺值 =================

    /// <summary>预设下拉 + 两条策略下拉 + 当前方案铺开。构造期调用一次。</summary>
    private void InitUi()
    {
        _loading = true;
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
            _loading = false;
        }
        RefreshUi();
    }

    /// <summary>把当前方案铺到窗口上：主键列表、三个功能键、基准音、音域、两条策略、预览图、符号说明。</summary>
    private void RefreshUi()
    {
        if (KeyList == null) return;
        _loading = true;
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

            // 预览图上的修饰键符号说明：名字跟着当前方案的功能键走，否则符号看不出含义
            if (TxtPreviewMods != null)
            {
                TxtPreviewMods.Text = "符号：↑ " + PreviewKeyName(_keymap.OctaveUp)
                                      + "　↓ " + PreviewKeyName(_keymap.OctaveDown)
                                      + "　# " + PreviewKeyName(_keymap.Sharp);
            }

            KeymapPreview.SetProfile(_keymap);
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_KEYMAP")))
                Dispatcher.UIThread.Post(ReportLayout, DispatcherPriority.Background);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// 仅在拍快照时打印一次布局尺寸，便于核对「默认方案 8 条是否一次全见」。
    /// 正常使用不设该环境变量，无输出、无行为。
    /// </summary>
    private void ReportLayout()
    {
        try
        {
            double vpW = KeyListScroll.Bounds.Width;
            double vpH = KeyListScroll.Bounds.Height;
            double contentH = KeyList.Bounds.Height;
            string line = $"[键位窗口] 窗口 {Bounds.Width:F0}x{Bounds.Height:F0}；"
                          + $"列表视口 {vpW:F0}x{vpH:F0}；列表内容 {KeyList.Bounds.Width:F0}x{contentH:F0}；"
                          + $"绑定 {_keymap.Keys.Count} 条；"
                          + $"列表需要滚动={contentH > vpH + 0.5}；全部可见={contentH <= vpH + 0.5}";
            Console.WriteLine(line);
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "midikey-snap.log"), line + "\n");
            }
            catch { }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("keymap layout report failed: " + ex.Message);
        }
    }

    /// <summary>把当前方案存盘并交回主窗（主窗负责同步设置记忆与卷帘）。</summary>
    private void Apply(string message)
    {
        try { _keymap.Save(); }
        catch (Exception ex) { message += $"（方案保存失败：{ex.Message}）"; }
        KeymapProfile.Current = _keymap;
        RefreshUi();
        _applyPath(_keymap, message);
    }

    private void Say(string message)
    {
        if (TxtKeymapHint != null) TxtKeymapHint.Text = message;
        _applyPath(null, message);
    }

    // ================= 显示名 =================

    /// <summary>预览图符号说明里用的功能键名；没设置就写「未设置」。</summary>
    private static string PreviewKeyName(string? key) =>
        string.IsNullOrEmpty(key) ? "未设置" : DisplayKey(key!);

    /// <summary>键名在按钮上的显示：null/空 = 未设置，逗号写成全角逗号。</summary>
    private static string KeyDisplayName(string? key) =>
        string.IsNullOrEmpty(key) ? "（未设置）" : DisplayKey(key!);

    private static string DisplayKey(string key) => key == "," ? "，" : key;

    // ================= 换方案 / 导入 / 导出 =================

    /// <summary>换内置预设：先把当前速度/移调/档位写回旧方案，再读新方案的值。</summary>
    private void KeymapCombo_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        string? name = KeymapCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(name) || name == _keymap.Name) return;

        KeymapProfile? found = KeymapProfile.Presets.FirstOrDefault(p => p.Name == name);
        if (found == null) return;

        _rememberSettings();
        string oldName = _keymap.Name;
        _keymap = CopyOf(found);
        KeymapProfile.Current = _keymap;
        try { _keymap.Save(); } catch { /* 保存失败由主窗日志告知 */ }
        string? extra = _applyProfileSettings(_keymap);
        RefreshUi();
        Say($"已换键位方案：{oldName} → {_keymap.Name}（主键 {_keymap.Keys.Count} 个）"
            + (string.IsNullOrEmpty(extra) ? "" : "。" + extra));
    }

    /// <summary>复制方案：不直接改预设定例，复制一份再编辑。</summary>
    private static KeymapProfile CopyOf(KeymapProfile src)
    {
        try { return src.Clone(); }
        catch { return KeymapProfile.Default; }
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
                Say("导出键位方案失败：拿不到目标路径。");
                return;
            }
            if (!_keymap.TryExportFile(path, out string error))
            {
                Say($"导出键位方案失败：{error}");
                return;
            }
            Say($"已导出键位方案：{System.IO.Path.GetFileName(path)}（主键 {_keymap.Keys.Count} 个）");
        }
        catch (Exception ex)
        {
            Say($"导出键位方案失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>导入方案 JSON。格式不对就显示中文提示，绝不崩。</summary>
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
                Say("导入键位方案失败：拿不到文件路径。");
                return;
            }

            // 解析与校验都由键位引擎负责，error 里已经是中文原因；失败时不动原有设置
            if (!KeymapProfile.TryImportFile(path, out KeymapProfile parsed, out string why))
            {
                Say($"导入失败：{why} 请选择本程序「导出方案」生成的文件。");
                return;
            }

            // 方案名可能变了：先存旧方案的速度/移调/档位，再读新方案的
            _rememberSettings();
            string oldName = _keymap.Name;
            _keymap = parsed;
            KeymapProfile.Current = _keymap;
            try { _keymap.Save(); } catch { /* 保存失败由主窗日志告知 */ }
            string? extra = _applyProfileSettings(_keymap);
            RefreshUi();

            string msg = $"已导入键位方案：{_keymap.Name}（主键 {_keymap.Keys.Count} 个，"
                         + $"音域 {Music.NoteName(_keymap.ResolveMinNote())} ~ {Music.NoteName(_keymap.ResolveMaxNote())}）";
            if (!string.Equals(oldName, _keymap.Name, StringComparison.Ordinal))
                msg += $"。方案名已变：{oldName} → {_keymap.Name}，速度 / 移调 / 档位已按新方案恢复";
            Say(msg);
        }
        catch (Exception ex)
        {
            Say($"导入键位方案失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ================= 主键列表 =================

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
        if (TxtKeymapHint != null) TxtKeymapHint.Text = "请按一个键来绑定（Esc 取消）";
    }

    /// <summary>
    /// 录入按键：挂在本窗口的 KeyDown 上。窗口关闭时这套状态随窗口一起销毁，
    /// 主窗不会残留「正在等按键」的状态。
    /// </summary>
    private void OnKeyDownCapture(object? sender, KeyEventArgs e)
    {
        if (!_keyCapturing) return;

        string? label = KeyLabelOf(e.Key);
        if (label == null)
        {
            if (e.Key == Key.Escape)
            {
                _keyCapturing = false;
                _keyCaptureTarget = null;
                Say("已取消本次绑定。");
                e.Handled = true;
            }
            return;   // 单独按修饰键等不可绑定的键：继续等
        }

        e.Handled = true;
        var target = _keyCaptureTarget;
        _keyCapturing = false;
        _keyCaptureTarget = null;
        if (TxtKeymapHint != null) TxtKeymapHint.Text = "";

        if (target is KeyBinding kb)
        {
            string old = kb.Key;
            kb.Key = label;
            Apply($"键位已改：{label}（原 {KeyDisplayName(old)}）");
        }
        else if (target is string which)
        {
            switch (which)
            {
                case "up": _keymap.OctaveUp = label; break;
                case "down": _keymap.OctaveDown = label; break;
                case "sharp": _keymap.Sharp = label; break;
            }
            Apply($"功能键已改：{which switch { "up" => "八度上", "down" => "八度下", _ => "升半音" }} = {label}");
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

    /// <summary>追加一个键：找一个还没被占用的半音偏移。</summary>
    private void KeyAdd_Click(object? sender, RoutedEventArgs e)
    {
        _keymap.Keys.Add(new KeyBinding { Key = "", Offset = NextFreeOffset() });
        Apply("已加一键，点它的键帽再按一个键即可绑定。");
    }

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
            Say("至少要留一个主键。");
            return;
        }
        _keymap.Keys.Remove(kb);
        Apply("已删除一键。");
    }

    // ================= 数值与策略 =================

    /// <summary>基准音 / 音域文本框：按回车或失焦时解析。</summary>
    private void KeymapNumber_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyNumbers(sender as TextBox);
        e.Handled = true;
    }

    private void KeymapNumber_LostFocus(object? sender, RoutedEventArgs e) => ApplyNumbers(sender as TextBox);

    private void ApplyNumbers(TextBox? box)
    {
        if (box == null || _loading) return;
        if (!int.TryParse(box.Text, out int v))
        {
            Say("请输入 0 ~ 127 之间的整数。");
            RefreshUi();
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
                Say("音域下限必须小于上限。");
                RefreshUi();
                return;
            }
            _keymap.MinNote = v;
        }
        else if (ReferenceEquals(box, TxtMaxNote))
        {
            if (v <= _keymap.ResolveMinNote())
            {
                Say("音域上限必须大于下限。");
                RefreshUi();
                return;
            }
            _keymap.MaxNote = v;
        }
        else
        {
            return;
        }
        Apply($"已更新：基准音 {Music.NoteName(_keymap.BaseNote)}，"
              + $"音域 {Music.NoteName(_keymap.ResolveMinNote())} ~ {Music.NoteName(_keymap.ResolveMaxNote())}");
    }

    /// <summary>超界策略 / 缺音策略下拉。</summary>
    private void KeymapPolicy_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _keymap.OutOfRange = OutOfRangeCombo.SelectedIndex == 1 ? OutOfRangeMode.Fold : OutOfRangeMode.Drop;
        _keymap.MissingNote = MissingNoteCombo.SelectedIndex == 1 ? MissingNoteMode.Drop : MissingNoteMode.Snap;
        Apply($"已更新策略：{(OutOfRangeCombo.SelectedIndex == 1 ? "超界折八度" : "超界丢音")}，"
              + $"{(MissingNoteCombo.SelectedIndex == 1 ? "缺音丢弃" : "缺音就近吸附")}");
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
