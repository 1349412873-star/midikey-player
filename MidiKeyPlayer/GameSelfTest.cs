using System.Text;
using Avalonia.Input;
using MidiKeyPlayer.Engine;
using MidiKeyPlayer.Midi;

namespace MidiKeyPlayer;

/// <summary>
/// 【开发用】纯逻辑自检：键位方案（内置预设、键名、绑定的键）与简谱换算。
///
/// 用法：设环境变量 <c>MIDIKEY_GAME_SELFTEST=1</c> 启动程序，或在值里给一个输出文件路径（以 .txt 结尾）：
/// <c>MIDIKEY_GAME_SELFTEST=E:\tmp\keymap-selftest.txt</c>。
/// 不写路径就写到 <c>%TEMP%\midikey-keymap-selftest.txt</c>。
/// 取值 <c>0</c> / <c>false</c> / <c>no</c> / <c>off</c> 等于没设，程序照常开窗。
///
/// 环境变量名带 GAME 是历史原因，不改：改名会让 run-selftest.ps1 与既有用法一起失效。
///
/// 走这条路时程序**不创建窗口、不注册热键、不碰按键与 MIDI 设备**，跑完直接退出，
/// 退出码 0 = 全过，1 = 有用例失败。和 DevUISnapshot / DevPreviewProbe 一样属于可删的开发件。
/// </summary>
internal static class GameSelfTest
{
    public const string EnvVar = "MIDIKEY_GAME_SELFTEST";

    private static readonly List<string> Lines = new();
    private static int _failed;

    public static bool Requested
    {
        get
        {
            string v = Environment.GetEnvironmentVariable(EnvVar) ?? "";
            return v.Length > 0 && !IsOffValue(v);
        }
    }

    /// <summary>
    /// 「明确关掉」的取值：0 / false / no / off（不分大小写）。
    /// <c>MIDIKEY_GAME_SELFTEST=0</c> 以前也会让程序不建窗口直接退出，与仓库其它开关的 <c>=="1"</c> 约定不一致。
    /// </summary>
    private static bool IsOffValue(string value)
    {
        string v = value.Trim();
        return v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)
            || v.Equals("no", StringComparison.OrdinalIgnoreCase)
            || v.Equals("off", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>环境变量给出的自定义报告路径（没有就是空串）。</summary>
    private static string CustomPath(string value)
    {
        string v = value.Trim();
        // "1" 是约定的「开」，没有路径含义
        if (v == "1" || IsOffValue(v)) return "";
        return v.Contains('\\') || v.Contains('/') ? v : "";
    }

    /// <summary>跑完全部用例，返回进程退出码。</summary>
    public static int Run()
    {
        string value = Environment.GetEnvironmentVariable(EnvVar) ?? "";
        string custom = CustomPath(value);
        string path = custom.Length > 0
            ? custom
            : Path.Combine(Path.GetTempPath(), "midikey-keymap-selftest.txt");
        // 报告一律写成 .txt：环境变量驱动的路径只在开发机上用，避免写出奇怪的后缀
        if (!path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            path = Path.Combine(Path.GetTempPath(), "midikey-keymap-selftest.txt");

        Lines.Add($"键位自检 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Lines.Add($"键位方案：{KeymapProfile.Default.Name}；内置预设 {KeymapProfile.Presets.Count} 套");
        Lines.Add("");

        try
        {
            TestPresetBinding();
            TestKeyMapNames();
            TestSolfegeNames();
        }
        catch (Exception ex)
        {
            _failed++;
            Lines.Add("FAIL 自检抛异常：" + ex);
        }

        Lines.Add("");
        Lines.Add(_failed == 0 ? "结果：全部通过" : $"结果：{_failed} 项失败");

        string text = string.Join(Environment.NewLine, Lines);
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
        }
        catch { /* 写不出去也要把退出码给对 */ }
        try { Console.WriteLine(text); } catch { }

        return _failed == 0 ? 0 : 1;
    }

    // ================= 内置方案 =================

    /// <summary>
    /// 内置预设的自检：套数下限、名字唯一且取得回、每套的键名与偏移不重复。
    ///
    /// 套数只断言下限、不写死：预设正在被改（6 套 → 3 套），写死数字一改就红。
    /// 「一个八度的半音阶方案」这类具体配置同样随预设一起改过，不再是硬要求，
    /// 只在方案存在时检查它内部自洽。
    /// </summary>
    private static void TestPresetBinding()
    {
        var presets = KeymapProfile.Presets;
        Check("预设：至少 3 套内置方案", presets.Count >= 3, $"实际 {presets.Count}");

        var names = new List<string>();
        int emptyName = 0;
        int dupName = 0;
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in presets)
        {
            if (string.IsNullOrWhiteSpace(p.Name)) { emptyName++; continue; }
            names.Add(p.Name);
            if (!seenNames.Add(p.Name)) dupName++;
        }
        Check("预设：每套都有名字", emptyName == 0, $"没名字 {emptyName} 套");
        Check("预设：名字两两不同", dupName == 0, $"重名 {dupName} 套");

        int dupKey = 0;
        int emptyKeys = 0;
        foreach (var p in presets)
        {
            var keys = p.Keys.Where(k => k != null && !string.IsNullOrWhiteSpace(k.Key))
                             .Select(k => KeymapProfile.CanonicalKeyName(k.Key))
                             .ToList();
            if (keys.Count == 0) emptyKeys++;
            if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Count) dupKey++;
        }
        Check("预设：每套都有按键", emptyKeys == 0, $"空方案 {emptyKeys} 套");
        Check("预设：每套的键名都不重复", dupKey == 0, $"重键 {dupKey} 套");

        // 界面换方案走的就是这条查表路径：名字取得回来，才能真的切过去
        var notFound = names.Where(n => KeymapProfile.PresetByName(n) == null).ToList();
        Check("预设：每套都能按名字取回", notFound.Count == 0, string.Join(" | ", notFound));

        // 默认方案是内置方案时也要能查回来，否则「回到默认」这条路径会落空。
        // 默认方案允许不是预设（用户自建方案可以当默认），所以用「取不回才失败」的写法。
        string defaultName = KeymapProfile.Default.Name;
        bool defaultPresent = names.Any(n => string.Equals(n, defaultName, StringComparison.Ordinal));
        Check("预设：默认方案的名字取得回",
              !string.IsNullOrWhiteSpace(defaultName)
              && (!defaultPresent || KeymapProfile.PresetByName(defaultName) != null),
              $"默认「{defaultName}」；内置：{string.Join(" | ", names)}");

        // 半音阶方案（偏移从 0 起连续覆盖 12 个半音）存在时，一个偏移只配一个键
        var chromatic = presets.Where(p => Covers(p, 12)).ToList();
        bool oneOffsetPerKey = chromatic.All(p =>
            p.Keys.Where(k => k != null).Select(k => k.Offset).Distinct().Count()
            == p.Keys.Count(k => k != null));
        Check("预设：半音阶方案里一个偏移只占一个键", oneOffsetPerKey,
              $"半音阶方案 {chromatic.Count} 套");
    }

    /// <summary>这套方案的偏移是否从 0 起连续覆盖 n 个半音。</summary>
    private static bool Covers(KeymapProfile p, int n)
    {
        var offsets = new HashSet<int>(p.Keys.Where(k => k != null).Select(k => k.Offset));
        for (int i = 0; i < n; i++) if (!offsets.Contains(i)) return false;
        return true;
    }

    // ================= 键名翻译 =================

    /// <summary>
    /// 键盘键 / 鼠标键 → 键名这条链路。每个键名都必须是方案认得的。
    ///
    /// 翻译函数原在 Input\GameKeyMap.cs（音游认键用的），音游删掉后搬到这里：
    /// 自检需要它，而方案自己的 IsKnownKeyName / KeyCharOf 才是被断言的对象。
    /// </summary>
    private static void TestKeyMapNames()
    {
        int bad = 0;
        for (Key k = Key.A; k <= Key.Z; k++)
            if (!IsUsableName(KeyNameOf(k))) bad++;
        Check("键名：A-Z 全部认得", bad == 0, $"认不出 {bad} 个");

        bad = 0;
        for (Key k = Key.D0; k <= Key.D9; k++)
            if (!IsUsableName(KeyNameOf(k))) bad++;
        Check("键名：主键盘 0-9 全部认得", bad == 0, $"认不出 {bad} 个");

        bad = 0;
        for (Key k = Key.NumPad0; k <= Key.NumPad9; k++)
            if (!IsUsableName(KeyNameOf(k))) bad++;
        Check("键名：小键盘 0-9 全部认得", bad == 0, $"认不出 {bad} 个");

        var punct = new (string Want, Key Code)[]
        {
            (",", Key.OemComma), (".", Key.OemPeriod), (";", Key.OemSemicolon),
            ("'", Key.OemQuotes), ("/", Key.OemQuestion), ("\\", Key.OemBackslash),
            ("[", Key.OemOpenBrackets), ("]", Key.OemCloseBrackets),
            ("-", Key.OemMinus), ("=", Key.OemPlus), ("`", Key.OemTilde),
        };
        bool punctOk = true;
        string punctBad = "";
        foreach (var (want, code) in punct)
        {
            string got = KeyNameOf(code);
            if (got != want || !IsUsableName(got))
            {
                punctOk = false;
                punctBad += $"{code}→「{got}」应为「{want}」 ";
            }
        }
        Check("键名：标点全部认得且对得上", punctOk, punctBad);

        // 同一条反斜杠键在 Avalonia 里有两个枚举值，必须映到同一个键名
        Check("键名：反斜杠两个枚举值同键名",
              KeyNameOf(Key.OemBackslash) == "\\" && KeyNameOf(Key.OemPipe) == "\\",
              $"OemBackslash=「{KeyNameOf(Key.OemBackslash)}」"
              + $"OemPipe=「{KeyNameOf(Key.OemPipe)}」");

        var named = new[]
        {
            Key.Space, Key.Enter, Key.Tab, Key.Back, Key.Escape,
            Key.LeftShift, Key.RightShift, Key.LeftCtrl, Key.RightCtrl,
            Key.LeftAlt, Key.RightAlt, Key.System,
            Key.PageUp, Key.PageDown, Key.Home, Key.End, Key.Insert, Key.Delete,
            Key.Up, Key.Down, Key.Left, Key.Right,
            Key.F1, Key.F2, Key.F6, Key.F12,
        };
        bool namedOk = true;
        string namedBad = "";
        foreach (var k in named)
        {
            string got = KeyNameOf(k);
            if (!IsUsableName(got)) { namedOk = false; namedBad += $"{k}→「{got}」 "; }
        }
        Check("键名：命名键全部认得", namedOk, namedBad);

        // 单独按 Alt：Windows 上报的是 Key.System，只能靠按住的修饰键认
        Check("键名：Key.System + Alt = Alt",
              KeyNameOf(Key.System, KeyModifiers.Alt) == "Alt",
              $"实际「{KeyNameOf(Key.System, KeyModifiers.Alt)}」");
        Check("键名：Key.System 不带修饰键也认作 Alt（老路径不丢键）",
              KeyNameOf(Key.System) == "Alt", $"实际「{KeyNameOf(Key.System)}」");
        Check("键名：Key.System + Ctrl = Ctrl",
              KeyNameOf(Key.System, KeyModifiers.Control) == "Ctrl",
              $"实际「{KeyNameOf(Key.System, KeyModifiers.Control)}」");
        Check("键名：Key.System + Shift = Shift",
              KeyNameOf(Key.System, KeyModifiers.Shift) == "Shift",
              $"实际「{KeyNameOf(Key.System, KeyModifiers.Shift)}」");

        Check("键名：鼠标三键认得",
              IsUsableName(MouseNameOf(PointerUpdateKind.LeftButtonPressed))
              && IsUsableName(MouseNameOf(PointerUpdateKind.RightButtonReleased))
              && IsUsableName(MouseNameOf(PointerUpdateKind.MiddleButtonPressed)),
              $"「{MouseNameOf(PointerUpdateKind.LeftButtonPressed)}」"
              + $"「{MouseNameOf(PointerUpdateKind.RightButtonReleased)}」"
              + $"「{MouseNameOf(PointerUpdateKind.MiddleButtonPressed)}」");

        // 鼠标键要能被方案认出来，否则鼠标修饰键永远匹配不上
        Check("键名：鼠标三键在方案里可用",
              IsUsableName("MouseLeft") && IsUsableName("MouseRight") && IsUsableName("MouseMiddle"),
              "MouseLeft / MouseRight / MouseMiddle");

        // 认不出的键必须给空串，调用方靠这个跳过
        Check("键名：认不出的键返回空串",
              KeyNameOf(Key.None) == "" && KeyNameOf((Key)0x7FFF) == "",
              $"None=「{KeyNameOf(Key.None)}」 0x7FFF=「{KeyNameOf((Key)0x7FFF)}」");
        Check("键名：认不出的鼠标事件返回空串",
              MouseNameOf(PointerUpdateKind.Other) == "",
              $"「{MouseNameOf(PointerUpdateKind.Other)}」");

        // 内置三套预设都不带功能键：这是设计（不是所有方案都需要，界面另有启用开关）。
        // 所以这里断言「空着，或者写的是认得的键名」，而不是强制三个都非空。
        var def = KeymapProfile.Default;
        Check("键名：方案里的功能键要么留空、要么认得",
              IsBlankOrKnown(def.OctaveDown) && IsBlankOrKnown(def.OctaveUp) && IsBlankOrKnown(def.Sharp),
              $"「{def.OctaveDown}」「{def.OctaveUp}」「{def.Sharp}」");

        bool modsEmpty = string.IsNullOrWhiteSpace(def.OctaveUp)
                         && string.IsNullOrWhiteSpace(def.OctaveDown)
                         && string.IsNullOrWhiteSpace(def.Sharp);
        Check("键名：功能键开关打开时必须有功能键", !def.ModifiersEnabled || !modsEmpty,
              $"开关={def.ModifiersEnabled} 三个都空={modsEmpty}");
        Check("键名：没有功能键的方案开关是关的", !modsEmpty || !def.ModifiersEnabled,
              $"开关={def.ModifiersEnabled}");

        // 键名 → 键名字符：命名键走私用区哨兵，单字符键原样返回
        Check("键名：命名字符是私用区哨兵",
              KeymapProfile.KeyCharOf("PageUp") != '\0' && KeymapProfile.KeyCharOf("PageUp") != 'P',
              $"实际 U+{(int)KeymapProfile.KeyCharOf("PageUp"):X4}");
        Check("键名：单字符键原样返回字符",
              KeymapProfile.KeyCharOf("Z") == 'Z' && KeymapProfile.KeyCharOf(",") == ',',
              "Z=" + KeymapProfile.KeyCharOf("Z") + " ,=" + KeymapProfile.KeyCharOf(","));
    }

    /// <summary>键名可用：非空、方案认得、能换出键名字符。</summary>
    private static bool IsUsableName(string name)
        => name.Length > 0 && KeymapProfile.IsKnownKeyName(name) && KeymapProfile.KeyCharOf(name) != '\0';

    /// <summary>功能键位置允许留空（方案不一定需要功能键）；填了就必须是认得的键名。</summary>
    private static bool IsBlankOrKnown(string? name)
        => string.IsNullOrWhiteSpace(name) || IsUsableName(KeymapProfile.CanonicalKeyName(name));

    // ================= 简谱换算 =================

    /// <summary>
    /// 简谱音高换算：中音区只有数字，高八度加上点，低八度加下点，最多两个点。
    /// 音域文字（SolfegeRange）与音名（NoteName）一起验，界面上到处在用。
    /// </summary>
    private static void TestSolfegeNames()
    {
        // 中音区（60 起）：do re mi fa sol la si = 1 2 3 4 5 6 7
        string[] middle = { "1", "2", "3", "4", "5", "6", "7" };
        bool middleOk = true;
        string middleBad = "";
        int[] degrees = { 60, 62, 64, 65, 67, 69, 71 };
        for (int i = 0; i < degrees.Length; i++)
        {
            string got = Music.SolfegeName(degrees[i]);
            if (got != middle[i]) { middleOk = false; middleBad += $"{degrees[i]}→「{got}」应为「{middle[i]}」 "; }
        }
        Check("简谱：中音区 1..7 对得上", middleOk, middleBad);

        // 中音 do 不带八度点
        Check("简谱：中音 do（60）= 1", Music.SolfegeName(60) == "1", $"实际「{Music.SolfegeName(60)}」");

        // 上点（U+02D9）是间距字符，界面的字体有字形；组合字符会渲染成豆腐块
        Check("简谱：高八度 do（72）= 1 加上点",
              Music.SolfegeName(72) == "1\u02D9", $"实际「{Music.SolfegeName(72)}」");
        Check("简谱：低八度 do（48）= 1 加下点",
              Music.SolfegeName(48) == "1.", $"实际「{Music.SolfegeName(48)}」");
        Check("简谱：八度点最多两个",
              Music.SolfegeName(96) == "1\u02D9\u02D9" && Music.SolfegeName(24) == "1..",
              $"96→「{Music.SolfegeName(96)}」 24→「{Music.SolfegeName(24)}」");

        // 升降号：61 = #1
        Check("简谱：升号音（61）= #1", Music.SolfegeName(61) == "#1", $"实际「{Music.SolfegeName(61)}」");

        // 音名与音域文字
        Check("简谱：音名 60 = C4", Music.NoteName(60) == "C4", $"实际「{Music.NoteName(60)}」");
        Check("简谱：音域低到高",
              Music.SolfegeRange(72, 60) == "1~1\u02D9",
              $"实际「{Music.SolfegeRange(72, 60)}」");

        // 预生成的查表结果必须与现算的一致（配表用，下标就是音高）
        Check("简谱：查表 SolfegeNames 与现算一致",
              Music.SolfegeNames.Length == 128 && Music.SolfegeNames[60] == "1"
              && Music.SolfegeNames[72] == "1\u02D9",
              $"长度 {Music.SolfegeNames.Length}");
    }

    // ================= 键名翻译实现 =================

    /// <summary>
    /// 键盘键 + 当时按住的修饰键 → 方案键名。认不出返回空串。
    ///
    /// Windows 上单独按 Alt 时 Avalonia 报的是 <see cref="Key.System"/>，不是
    /// <see cref="Key.LeftAlt"/> / <see cref="Key.RightAlt"/>；Ctrl / Shift 也会走同一个值。
    /// 这时只能靠按住的修饰键认出来。少了这一支，绑在 Alt 上的音键永远匹配不上。
    /// </summary>
    private static string KeyNameOf(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        if (key == Key.System)
        {
            if (modifiers.HasFlag(KeyModifiers.Control)) return "Ctrl";
            if (modifiers.HasFlag(KeyModifiers.Shift)) return "Shift";
            return "Alt";
        }

        if (key >= Key.A && key <= Key.Z)
            return ((char)('A' + (key - Key.A))).ToString();

        if (key >= Key.D0 && key <= Key.D9)
            return ((char)('0' + (key - Key.D0))).ToString();

        if (key >= Key.NumPad0 && key <= Key.NumPad9)
            return "NumPad" + (key - Key.NumPad0);

        if (key >= Key.F1 && key <= Key.F12)
            return "F" + (key - Key.F1 + 1);

        return key switch
        {
            Key.Space => "Space",
            Key.Enter => "Enter",
            Key.Tab => "Tab",
            Key.Back => "Back",
            Key.Escape => "Escape",
            Key.LeftShift or Key.RightShift => "Shift",
            Key.LeftCtrl or Key.RightCtrl => "Ctrl",
            Key.LeftAlt or Key.RightAlt => "Alt",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Home => "Home",
            Key.End => "End",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",

            // 标点：与 KeymapProfile 里允许的单字符键一一对应
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemQuestion => "/",
            // 同一条反斜杠键在两处枚举值不同：键位窗口录的是 OemPipe，这里原来只认
            // OemBackslash。两个枚举值都映到同一个键名，免得一边录的键另一边收不到。
            Key.OemBackslash or Key.OemPipe => "\\",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemTilde => "`",

            _ => "",
        };
    }

    /// <summary>鼠标键 → 方案键名。非鼠标事件返回空串。</summary>
    private static string MouseNameOf(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => "MouseLeft",
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => "MouseRight",
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => "MouseMiddle",
        _ => "",
    };

    // ================= 断言 =================

    private static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) _failed++;
        Lines.Add((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? $"   [{detail}]" : ""));
    }
}
