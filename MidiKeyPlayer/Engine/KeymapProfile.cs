using System.Text.Json;
using System.Text.Json.Serialization;
using MidiKeyPlayer.Persist;

namespace MidiKeyPlayer.Engine;

/// <summary>
/// 【兼容保留，界面不再提供】超界处理：丢音 / 就近折八度。JSON 里写 <c>drop</c> / <c>fold</c>。
/// 新版本固定按 <see cref="Drop"/> 处理：超出可弹范围的音一律不弹。读到老文件的 <c>fold</c> 只写一条日志。
/// </summary>
[JsonConverter(typeof(OutOfRangeModeConverter))]
public enum OutOfRangeMode { Drop, Fold }

/// <summary>
/// 缺音处理：能弹范围里缺一个半音时怎么办。JSON 里写 <c>skip</c> / <c>up</c> / <c>down</c>。
/// 老文件的 <c>snap</c> 读成 <see cref="Up"/>，<c>drop</c> 读成 <see cref="Skip"/>，并写日志说明已升级。
/// </summary>
[JsonConverter(typeof(MissingNoteModeConverter))]
public enum MissingNoteMode { Skip, Up, Down }

/// <summary>
/// 一个主键：物理键名 + 相对 <see cref="KeymapProfile.BaseNote"/> 的半音偏移。
/// JSON 里写成两元素数组 <c>["Z",0]</c>，由 <see cref="KeyBindingConverter"/> 负责。
/// </summary>
[JsonConverter(typeof(KeyBindingConverter))]
public sealed class KeyBinding
{
    /// <summary>键名。单字符键写 "Z"、","、"1"；命名键写 "PageUp"、"MouseLeft"。</summary>
    public string Key { get; set; } = "";

    /// <summary>相对 <see cref="KeymapProfile.BaseNote"/> 的半音偏移。</summary>
    public int Offset { get; set; }

    public override string ToString() => $"{Key}{(Offset >= 0 ? "+" : "")}{Offset}";
}

/// <summary>方案 JSON 读不动时的异常。消息是给人看的中文。</summary>
public sealed class KeymapFormatException : Exception
{
    public KeymapFormatException(string message) : base(message) { }
    public KeymapFormatException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// 键位方案：把「键名 → 半音偏移」与修饰键、缺音策略全部交给配置。
///
/// 活动方案是 <see cref="Current"/>（全局一份）。文件在 %LOCALAPPDATA%\MidiKeyPlayer\keymap.json。
/// 序列化必须走源生成上下文 <see cref="KeymapJson"/>：发布开了 PublishTrimmed，
/// 反射式重载在裁剪下会抛异常。
/// </summary>
public sealed class KeymapProfile
{
    /// <summary>
    /// 默认方案名。名字 = 键位数 / 占几排 / 几个八度，与 <see cref="SchemeNameOf"/> 算出来的一致。
    /// 默认方案：Z X C V B N M 加逗号（8 键，都在 ZXCV 排），左右键各移一个八度，48..85。
    /// </summary>
    public const string DefaultName = "8 键 1 排 3 个八度";

    /// <summary>当前格式版本。读到更大的版本号就是不认识的格式。</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = DefaultName;
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public int BaseNote { get; set; } = 60;
    public List<KeyBinding> Keys { get; set; } = new();
    public string? OctaveUp { get; set; } = "MouseRight";
    public string? OctaveDown { get; set; } = "MouseLeft";
    public string? Sharp { get; set; } = "MouseMiddle";

    /// <summary>
    /// 【兼容字段】老文件里写过音域下限。新版本的音域一律由键位推导（见 <see cref="ResolveMinNote"/>），
    /// 这里读到什么值都不参与判断，只在写盘时原样保留。
    /// </summary>
    public int? MinNote { get; set; }

    /// <summary>【兼容字段】老文件里写过音域上限。含义同 <see cref="MinNote"/>。</summary>
    public int? MaxNote { get; set; }

    /// <summary>【兼容字段】超出可弹范围的音固定不弹，这个字段不再影响行为。</summary>
    public OutOfRangeMode OutOfRange { get; set; } = OutOfRangeMode.Drop;

    /// <summary>缺音处理：跳过 / 用高半音代替 / 用低半音代替。界面上的「半音怎么处理？」。</summary>
    public MissingNoteMode MissingNote { get; set; } = MissingNoteMode.Up;

    // ================= 位置与活动方案 =================

    private static string DirPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MidiKeyPlayer");

    /// <summary>方案文件路径：%LOCALAPPDATA%\MidiKeyPlayer\keymap.json。</summary>
    public static string FilePath => Path.Combine(DirPath, "keymap.json");

    private static KeymapProfile? _current;

    /// <summary>
    /// 全局活动方案。赋 null 等于恢复默认，永远不会把 null 放出去。
    /// 首次访问时读盘（keymap.json → 设置里的方案名 → 默认方案）。
    /// </summary>
    public static KeymapProfile Current
    {
        get => _current ??= Load();
        set => _current = value ?? BuildDefault();
    }

    // ================= 内置预设 =================

    private static IReadOnlyList<KeymapProfile>? _presets;

    /// <summary>
    /// 4 套内置预设。第一项就是默认方案（旧行为）。名字统一由 <see cref="SchemeNameOf"/> 算出来：
    /// 「N 键 M 排 K 个八度」。只读用；要改先 <see cref="Clone"/>。
    /// </summary>
    public static IReadOnlyList<KeymapProfile> Presets => _presets ??= BuildPresets();

    /// <summary>默认方案（第 1 套）。每次取都是新副本，改它不影响内置预设。</summary>
    public static KeymapProfile Default => BuildDefault();

    /// <summary>
    /// 默认方案（也是第 1 套预设）：Z X C V B N M 与逗号；左键降八度、右键升八度、中键升半音。
    /// 能弹范围由键位推导得到 48..85。每次取都是新副本，改它不影响内置预设。
    /// </summary>
    private static KeymapProfile BuildDefault() => new()
    {
        Version = CurrentVersion,
        Name = DefaultName,
        Author = "",
        Description = "Z X C V B N M = do..si，逗号 = 高八度 do；左键降八度、右键升八度、中键升半音",
        BaseNote = 60,
        Keys = new List<KeyBinding>
        {
            new() { Key = "Z", Offset = 0 },
            new() { Key = "X", Offset = 2 },
            new() { Key = "C", Offset = 4 },
            new() { Key = "V", Offset = 5 },
            new() { Key = "B", Offset = 7 },
            new() { Key = "N", Offset = 9 },
            new() { Key = "M", Offset = 11 },
            new() { Key = ",", Offset = 12 },
        },
        OctaveUp = "MouseRight",
        OctaveDown = "MouseLeft",
        Sharp = "MouseMiddle",
        MissingNote = MissingNoteMode.Up,
    };

    /// <summary>第 2 套：只要 7 个字母键，一个八度自然音，没有修饰键。能弹 60..71。</summary>
    private static KeymapProfile BuildPreset7() => new()
    {
        Version = CurrentVersion,
        Description = "Z X C V B N M = do..si，一个八度自然音；没有八度键与升半音键",
        BaseNote = 60,
        Keys = new List<KeyBinding>
        {
            new() { Key = "Z", Offset = 0 },
            new() { Key = "X", Offset = 2 },
            new() { Key = "C", Offset = 4 },
            new() { Key = "V", Offset = 5 },
            new() { Key = "B", Offset = 7 },
            new() { Key = "N", Offset = 9 },
            new() { Key = "M", Offset = 11 },
        },
        OctaveUp = "",
        OctaveDown = "",
        Sharp = "",
        MissingNote = MissingNoteMode.Up,
    };

    /// <summary>第 3 套：三排各 5 键，跨约两个八度自然音，没有修饰键。能弹 60..79。</summary>
    private static KeymapProfile BuildPreset15() => new()
    {
        Version = CurrentVersion,
        Description = "三排各 5 键，覆盖约两个八度自然音，没有修饰键",
        BaseNote = 60,
        Keys = new List<KeyBinding>
        {
            new() { Key = "Q", Offset = 0 },
            new() { Key = "W", Offset = 2 },
            new() { Key = "E", Offset = 4 },
            new() { Key = "R", Offset = 5 },
            new() { Key = "T", Offset = 7 },
            new() { Key = "A", Offset = 7 },
            new() { Key = "S", Offset = 9 },
            new() { Key = "D", Offset = 11 },
            new() { Key = "F", Offset = 12 },
            new() { Key = "G", Offset = 14 },
            new() { Key = "Z", Offset = 12 },
            new() { Key = "X", Offset = 14 },
            new() { Key = "C", Offset = 16 },
            new() { Key = "V", Offset = 17 },
            new() { Key = "B", Offset = 19 },
        },
        OctaveUp = null,
        OctaveDown = null,
        Sharp = null,
        MissingNote = MissingNoteMode.Up,
    };

    /// <summary>
    /// 第 4 套：三排自然音阶，一排一个八度，共 23 个键。
    /// Q W E R T Y U I / A S D F G H J K / Z X C V B N M；能弹 60..95（约三个八度）。
    /// </summary>
    private static KeymapProfile BuildPreset23() => new()
    {
        Version = CurrentVersion,
        Description = "三排自然音阶，一排一个八度；没有修饰键",
        BaseNote = 60,
        Keys = new List<KeyBinding>
        {
            // 第一排（QWERTY 排）：C4..C5
            new() { Key = "Q", Offset = 0 },
            new() { Key = "W", Offset = 2 },
            new() { Key = "E", Offset = 4 },
            new() { Key = "R", Offset = 5 },
            new() { Key = "T", Offset = 7 },
            new() { Key = "Y", Offset = 9 },
            new() { Key = "U", Offset = 11 },
            new() { Key = "I", Offset = 12 },
            // 第二排（ASDF 排）：C5..C6
            new() { Key = "A", Offset = 12 },
            new() { Key = "S", Offset = 14 },
            new() { Key = "D", Offset = 16 },
            new() { Key = "F", Offset = 17 },
            new() { Key = "G", Offset = 19 },
            new() { Key = "H", Offset = 21 },
            new() { Key = "J", Offset = 23 },
            new() { Key = "K", Offset = 24 },
            // 第三排（ZXCV 排）：C6..B6
            new() { Key = "Z", Offset = 24 },
            new() { Key = "X", Offset = 26 },
            new() { Key = "C", Offset = 28 },
            new() { Key = "V", Offset = 29 },
            new() { Key = "B", Offset = 31 },
            new() { Key = "N", Offset = 33 },
            new() { Key = "M", Offset = 35 },
        },
        OctaveUp = null,
        OctaveDown = null,
        Sharp = null,
        MissingNote = MissingNoteMode.Up,
    };

    /// <summary>
    /// 方案名：「N 键 M 排 K 个八度」。
    /// N = 键位数；M = 这些键在物理键盘上占几排；K = 能弹音域的八度数（向上取整）。
    /// K 由「键位 × 八度键」能到达的音高张角算出，升半音键只在音域内补半音，不扩展边界。
    /// </summary>
    public static string SchemeNameOf(KeymapProfile profile)
    {
        int keys = profile.Keys.Count(k => k != null && !string.IsNullOrWhiteSpace(k.Key));
        return $"{keys} 键 {RowCountOf(profile)} 排 {OctaveCountOf(profile)} 个八度";
    }

    /// <summary>这些键在物理键盘上占几排。认不出排的键（鼠标、命名键）不计数。</summary>
    private static int RowCountOf(KeymapProfile profile)
    {
        var rows = new HashSet<int>();
        foreach (var k in profile.Keys)
        {
            if (k == null) continue;
            int row = PhysicalRowOf(k.Key);
            if (row >= 0) rows.Add(row);
        }
        return Math.Max(1, rows.Count);
    }

    /// <summary>键名 → 物理排号：0 数字排、1 QWERTY 排、2 ASDF 排、3 ZXCV 排；-1 表示认不出。</summary>
    private static int PhysicalRowOf(string? key)
    {
        string name = CanonicalKeyName(key);
        if (name.Length != 1) return -1;
        char c = name[0];
        if ("1234567890-=".Contains(c)) return 0;
        if ("QWERTYUIOP[]\\".Contains(c)) return 1;
        if ("ASDFGHJKL;'".Contains(c)) return 2;
        if ("ZXCVBNM,./".Contains(c)) return 3;
        return -1;
    }

    /// <summary>
    /// 能弹音域的八度数：最短键位到最长键位之间，加上八度键能挪动的量，再向上取整。
    /// 一个音都认不出时算 1 个八度。
    /// </summary>
    private static int OctaveCountOf(KeymapProfile profile)
    {
        int min = int.MaxValue, max = int.MinValue;
        foreach (var k in profile.Keys)
        {
            if (k == null || !IsKnownKeyName(k.Key)) continue;
            if (k.Offset < min) min = k.Offset;
            if (k.Offset > max) max = k.Offset;
        }
        if (min > max) return 1;

        int lo = min - (string.IsNullOrWhiteSpace(profile.OctaveDown) ? 0 : 12);
        int hi = max + (string.IsNullOrWhiteSpace(profile.OctaveUp) ? 0 : 12);
        return Math.Max(1, (int)Math.Ceiling((hi - lo) / 12.0));
    }

    /// <summary>
    /// 4 套内置预设。名字一律由 <see cref="SchemeNameOf"/> 算出来，不手写。
    /// 第 1 套的名字必须等于 <see cref="DefaultName"/>，不等就写日志（说明有人改了键位没改常量）。
    /// </summary>
    private static IReadOnlyList<KeymapProfile> BuildPresets()
    {
        var list = new List<KeymapProfile>
        {
            Named(BuildDefault()),     // 第 1 套 = 默认方案（旧行为）
            Named(BuildPreset7()),     // 第 2 套：7 键单排自然音阶
            Named(BuildPreset15()),    // 第 3 套：15 键三排
            Named(BuildPreset23()),    // 第 4 套：23 键三排（三排自然音阶）
        };
        if (!string.Equals(list[0].Name, DefaultName, StringComparison.Ordinal))
            LogFile.Append($"[键位] 默认方案名算出来是「{list[0].Name}」，与常量「{DefaultName}」不同，请同步。");
        return list;
    }

    private static KeymapProfile Named(KeymapProfile profile)
    {
        profile.Name = SchemeNameOf(profile);
        return profile;
    }

    /// <summary>按方案名找内置预设。找不到返回 null。老名字（改名前 / 已删掉）先过一次别名表。</summary>
    public static KeymapProfile? PresetByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (var p in Presets)
            if (string.Equals(p.Name, name, StringComparison.Ordinal)) return p;

        string? now = AliasOf(name);
        if (now != null)
            foreach (var p in Presets)
                if (string.Equals(p.Name, now, StringComparison.Ordinal)) return p;
        return null;
    }

    // ================= 老方案名（升级用） =================

    /// <summary>改过名的预设：老名字 → 现在的名字。老用户设置里存的还是老名字。</summary>
    private static readonly Dictionary<string, string> LegacyPresetAlias = new(StringComparer.Ordinal)
    {
        ["8 键单排（含高八度 do，默认）"] = "8 键 1 排 3 个八度",
        ["7 键单排自然音阶"] = "7 键 1 排 1 个八度",
        ["15 键三排"] = "15 键 3 排 2 个八度",
    };

    /// <summary>已经删掉的预设：读到这些名字就回退到默认方案。</summary>
    private static readonly HashSet<string> RemovedPresetNames = new(StringComparer.Ordinal)
    {
        "12 键半音阶双排",
        "5 键极简",
        "宽音域 8 八度折叠",
    };

    /// <summary>老名字对应的新名字（改过名的那几套）。不是老名字就返回 null。</summary>
    public static string? AliasOf(string? name)
        => !string.IsNullOrWhiteSpace(name) && LegacyPresetAlias.TryGetValue(name!, out string? now) ? now : null;

    /// <summary>这个名字是不是「已经删掉的预设」。</summary>
    public static bool IsRemovedPresetName(string? name)
        => !string.IsNullOrWhiteSpace(name) && RemovedPresetNames.Contains(name!);

    /// <summary>
    /// 读到一个方案名之后做一次升级：改过名的预设就地改名（键位不动）；删掉的预设回退到默认方案。
    /// 返回要用的方案，log 里是中文说明（空串 = 不用记）。
    /// </summary>
    private static KeymapProfile UpgradeLegacyName(KeymapProfile profile, out string log)
    {
        log = "";
        string name = profile.Name ?? "";

        string? now = AliasOf(name);
        if (now != null)
        {
            profile.Name = now;
            log = $"[键位] 方案名已升级：{name} → {now}";
            return profile;
        }

        if (IsRemovedPresetName(name))
        {
            log = $"[键位] 方案「{name}」已经删掉，回退到默认方案「{DefaultName}」。";
            return BuildDefault();
        }

        return profile;
    }

    // ================= 方案库（内置 + 用户自己存的文件） =================

    /// <summary>
    /// 用户方案文件所在目录：%LOCALAPPDATA%\MidiKeyPlayer\schemes\。
    /// 一个方案一个文件，文件名（去掉扩展名）= 方案名。
    /// </summary>
    public static string SchemesDir =>
        Path.Combine(DirPath, "schemes");

    /// <summary>某个方案名对应的文件路径。名字里的非法字符换成下划线。</summary>
    public static string SchemeFilePath(string? name)
    {
        string safe = (name ?? "").Trim();
        foreach (char bad in Path.GetInvalidFileNameChars()) safe = safe.Replace(bad, '_');
        if (safe.Length == 0) safe = "未命名方案";
        return Path.Combine(SchemesDir, safe + ".json");
    }

    /// <summary>schemes 目录下已有的方案文件名（不含扩展名）。目录不在或读不动就返回空。</summary>
    private static List<string> SchemeFiles()
    {
        var list = new List<string>();
        try
        {
            if (!Directory.Exists(SchemesDir)) return list;
            foreach (var path in Directory.GetFiles(SchemesDir, "*.json"))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrWhiteSpace(name)) list.Add(name);
            }
        }
        catch (Exception ex)
        {
            LogFile.Append("[键位] 列 schemes 方案文件失败：" + ex.Message);
        }
        return list;
    }

    /// <summary>
    /// 全部可用方案名：内置 4 套在前（按 Presets 的顺序），用户方案文件在后（按名字排序）。
    /// 名字相同的只留一次。下拉框直接用这个列表。
    /// </summary>
    public static IReadOnlyList<string> ListSchemeNames()
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in Presets)
            if (!string.IsNullOrWhiteSpace(p.Name) && seen.Add(p.Name)) names.Add(p.Name);

        var files = SchemeFiles();
        files.Sort(StringComparer.Ordinal);
        foreach (string f in files)
            if (seen.Add(f)) names.Add(f);

        return names;
    }

    /// <summary>这个名字是不是内置预设。内置方案不能删除、不能重命名、不能被覆盖。</summary>
    public static bool IsBuiltInSchemeName(string? name) => PresetByName(name) != null;

    /// <summary>这个名字已经被某个方案占用（内置或用户文件）。</summary>
    public static bool SchemeNameExists(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (IsBuiltInSchemeName(name)) return true;
        try
        {
            return File.Exists(SchemeFilePath(name));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 按名字载入一个方案。内置名（含老名字）走内置预设，其余读 schemes\&lt;名字&gt;.json。
    /// 文件不在或读不动返回 null，并写出可读原因。绝不抛异常。
    /// </summary>
    public static KeymapProfile? LoadByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var preset = PresetByName(name);
        if (preset != null) return preset.Clone();

        string path = SchemeFilePath(name);
        try
        {
            if (!File.Exists(path))
            {
                LogFile.Append($"[键位] 没有方案文件：{path}");
                return null;
            }
            return FromJson(File.ReadAllText(path));
        }
        catch (KeymapFormatException ex)
        {
            LogFile.Append($"[键位] 方案文件格式不对（{path}）：{ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            LogFile.Append($"[键位] 读方案文件失败（{path}）：{ex.Message}");
            return null;
        }
    }

    // ================= 读盘 / 写盘 =================

    /// <summary>
    /// 读活动方案。顺序：keymap.json → 设置里记的方案名（用户文件，再内置预设）→ 默认方案。
    /// 读到改名前的预设名就地改名；读到已经删掉的预设名回退到默认方案，两种情况都写日志。
    /// 任何失败都只写日志，返回默认方案的副本，绝不抛异常。
    /// </summary>
    public static KeymapProfile Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = FromJson(File.ReadAllText(FilePath));
                if (!string.IsNullOrWhiteSpace(loaded.Name))
                {
                    var fixedUp = UpgradeLegacyName(loaded, out string note);
                    if (note.Length > 0)
                    {
                        LogFile.Append(note);
                        try { fixedUp.Save(); } catch { /* 存不动只影响下次启动 */ }
                    }
                    return fixedUp;
                }
                LogFile.Append("[键位] keymap.json 没有方案名，用默认方案。");
            }
        }
        catch (Exception ex)
        {
            LogFile.Append("[键位] 读取 keymap.json 失败，用默认方案：" + ex.Message);
        }

        try
        {
            string want = AppConfig.Load().KeymapName;
            if (!string.IsNullOrWhiteSpace(want))
            {
                var byName = LoadByName(want);
                if (byName != null)
                {
                    LogFile.Append($"[键位] 按设置载入方案：{byName.Name}");
                    return byName;
                }
            }
        }
        catch (Exception ex)
        {
            LogFile.Append("[键位] 按设置选方案失败，用默认方案：" + ex.Message);
        }

        return BuildDefault();
    }

    /// <summary>把当前方案写回 keymap.json。失败只写日志，不抛异常。</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DirPath);
            File.WriteAllText(FilePath, ToJson());
        }
        catch (Exception ex)
        {
            LogFile.Append("[键位] 保存 keymap.json 失败：" + ex.Message);
        }
    }

    /// <summary>导出到用户选的路径。返回 false 时 error 里是中文原因。</summary>
    public bool TryExportFile(string path, out string error)
    {
        error = "";
        try
        {
            string dir = Path.GetDirectoryName(path) ?? "";
            if (dir.Length > 0) Directory.CreateDirectory(dir);
            File.WriteAllText(path, ToJson());
            return true;
        }
        catch (Exception ex)
        {
            error = $"写文件失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 从文件导入。成功时给出新方案；失败时 error 里是中文原因，调用方的原有设置不动。
    /// </summary>
    public static bool TryImportFile(string path, out KeymapProfile profile, out string error)
    {
        profile = BuildDefault();
        error = "";
        try
        {
            return TryFromJson(File.ReadAllText(path), out profile, out error);
        }
        catch (Exception ex)
        {
            error = $"读文件失败：{ex.Message}";
            return false;
        }
    }

    // ================= JSON =================

    /// <summary>序列化成一个 JSON 文本。</summary>
    public string ToJson() => JsonSerializer.Serialize(this, KeymapJson.Default.KeymapProfile);

    /// <summary>
    /// 解析一份方案 JSON。格式不认识就抛 <see cref="KeymapFormatException"/>（消息是中文）。
    /// 界面导入要接住它，并保留原有设置。
    /// </summary>
    public static KeymapProfile FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new KeymapFormatException("文件是空的。");

        KeymapProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize(json, KeymapJson.Default.KeymapProfile);
        }
        catch (JsonException ex)
        {
            throw new KeymapFormatException("不是有效的 JSON：" + ex.Message, ex);
        }

        if (profile == null)
            throw new KeymapFormatException("文件里没有方案内容。");
        if (profile.Version > CurrentVersion)
            throw new KeymapFormatException($"方案版本 {profile.Version} 高于本程序支持的 {CurrentVersion}。");
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new KeymapFormatException("方案缺少名称。");
        if (profile.Keys == null || profile.Keys.Count == 0)
            throw new KeymapFormatException("方案里一个主键都没有。");

        profile.Keys.RemoveAll(k => k == null);
        if (profile.Keys.Count == 0)
            throw new KeymapFormatException("方案里的主键都不可用。");

        foreach (var k in profile.Keys)
        {
            k.Key = (k.Key ?? "").Trim();
            if (k.Key.Length == 0)
                throw new KeymapFormatException("方案里有主键没写键名。");
            if (!IsKnownKeyName(k.Key))
                throw new KeymapFormatException($"不认识的键名「{k.Key}」。");
        }

        foreach (var (name, label) in new[]
                 {
                     (profile.OctaveUp, "octaveUp"),
                     (profile.OctaveDown, "octaveDown"),
                     (profile.Sharp, "sharp"),
                 })
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!IsKnownKeyName(name))
                throw new KeymapFormatException($"{label} 的键名「{name}」不认识。");
        }

        profile.Version = profile.Version <= 0 ? CurrentVersion : profile.Version;
        profile.BaseNote = Math.Clamp(profile.BaseNote, 0, 127);
        // minNote / maxNote 只做兼容保留：夹一下范围就放着，音域一律由键位推导，不再据此报错。
        if (profile.MinNote.HasValue) profile.MinNote = Math.Clamp(profile.MinNote.Value, 0, 127);
        if (profile.MaxNote.HasValue) profile.MaxNote = Math.Clamp(profile.MaxNote.Value, 0, 127);
        return profile;
    }

    /// <summary>解析一份方案 JSON，失败时用中文说明原因，不抛异常。</summary>
    public static bool TryFromJson(string json, out KeymapProfile profile, out string error)
    {
        try
        {
            profile = FromJson(json);
            error = "";
            return true;
        }
        catch (KeymapFormatException ex)
        {
            profile = BuildDefault();
            error = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            profile = BuildDefault();
            error = "解析失败：" + ex.Message;
            return false;
        }
    }

    // ================= 音域（由键位推导） =================
    //
    // 能弹的音高集合 = 所有键位在基准音与修饰键作用下实际能到达的音高：
    //   音高 = BaseNote + 键偏移 + 12 × 八度档（有升/降八度键才有这一档）
    //   升半音键再 +1。
    // 集合的上下限就是音域，界面不再填 minNote / maxNote。

    /// <summary>基准音所在的八度编号（C4 = 4）。</summary>
    public int BaseOctave => BaseNote / 12 - 1;

    /// <summary>可演奏最低音：所有键位配上八度键能到的最低音。</summary>
    public int ResolveMinNote() => ReachableExtent().Lo;

    /// <summary>可演奏最高音：所有键位配上八度键与升半音键能到的最高音。</summary>
    public int ResolveMaxNote() => ReachableExtent().Hi;

    /// <summary>能弹范围的上下限（含端点）。一个可用的键都没有时返回基准音。</summary>
    public (int Lo, int Hi) ReachableExtent()
    {
        if (!TryReachableBounds(out int lo, out int hi))
            return (Math.Clamp(BaseNote, 0, 127), Math.Clamp(BaseNote, 0, 127));
        if (lo > hi) (lo, hi) = (hi, lo);
        return (Math.Clamp(lo, 0, 127), Math.Clamp(hi, 0, 127));
    }

    /// <summary>把键位、八度键、升半音键铺开，算出能到达的最低与最高音高。</summary>
    private bool TryReachableBounds(out int lo, out int hi)
    {
        lo = 0;
        hi = 0;
        bool any = false;

        bool canUp = !string.IsNullOrWhiteSpace(OctaveUp);
        bool canDown = !string.IsNullOrWhiteSpace(OctaveDown);
        bool canSharp = !string.IsNullOrWhiteSpace(Sharp);

        foreach (var k in Keys)
        {
            if (k == null || string.IsNullOrWhiteSpace(k.Key)) continue;
            if (!IsKnownKeyName(k.Key)) continue;

            for (int mod = -1; mod <= 1; mod++)
            {
                if (mod < 0 && !canDown) continue;
                if (mod > 0 && !canUp) continue;

                int low = BaseNote + k.Offset + 12 * mod;
                int high = canSharp ? low + 1 : low;
                if (!any) { lo = low; hi = high; any = true; continue; }
                if (low < lo) lo = low;
                if (high > hi) hi = high;
            }
        }
        return any;
    }

    /// <summary>
    /// 该音高是否在能弹范围内（含端点）。范围是上下限之间的区间：
    /// 区间里的半音按 <see cref="MissingNote"/> 处理，区间之外固定不弹。
    /// </summary>
    public bool InRange(int pitch)
    {
        var (lo, hi) = ReachableExtent();
        return pitch >= lo && pitch <= hi;
    }

    /// <summary>
    /// 就近折八度：把音域外的音上下各折若干个八度，取离原音最近、且落在音域内的那个。
    /// 距离相同时取更低的音。折不动时原样返回。
    /// </summary>
    public int FoldIntoRange(int pitch)
    {
        int lo = ResolveMinNote();
        int hi = ResolveMaxNote();
        if (lo > hi) (lo, hi) = (hi, lo);
        if (pitch >= lo && pitch <= hi) return pitch;

        int best = pitch;
        int bestDist = int.MaxValue;
        for (int k = -10; k <= 10; k++)
        {
            int c = pitch + 12 * k;
            if (c < 0 || c > 127) continue;
            if (c < lo || c > hi) continue;
            int d = Math.Abs(c - pitch);
            if (d < bestDist || (d == bestDist && c < best))
            {
                best = c;
                bestDist = d;
            }
        }
        return bestDist == int.MaxValue ? pitch : best;
    }

    // ================= 查表 =================

    /// <summary>某个键名对应的半音偏移（取第一项）。找不到返回 false。</summary>
    public bool TryKeyByName(string keyName, out int semitoneOffset)
    {
        semitoneOffset = 0;
        if (string.IsNullOrWhiteSpace(keyName)) return false;
        foreach (var k in Keys)
        {
            if (k == null) continue;
            if (string.Equals(k.Key, keyName, StringComparison.OrdinalIgnoreCase))
            {
                semitoneOffset = k.Offset;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 把一个音高（方案基准八度下的绝对 MIDI 音高）映射到「按键 + 八度档 + 升半音」。
    ///
    /// 顺序：范围外的音固定不弹（返回 false）；范围里的音先在键表里找候选：
    /// 候选 = BaseNote + 键偏移 + 12×八度档（+ 升半音键则再 +1）。
    /// 精确命中就用它；没有精确命中时按 <see cref="MissingNote"/> 处理：
    /// Skip = 这个半音不弹；Up = 用上面那个音代替；Down = 用下面那个音代替。
    ///
    /// 目标音相同的候选之间优先顺序固定：不用升半音键 → 键偏移小 → 八度档绝对值小 → 键表里靠前。
    /// 这套顺序让默认方案与旧版硬编码行为一致。
    /// </summary>
    public bool TryKeyOfPitch(int pitch, out string key, out int octaveOffset, out bool sharp)
        => TryKeyOfPitch(pitch, out key, out octaveOffset, out sharp, out _);

    /// <summary>同上，另给出实际发声音高（缺音代替后可能与入参不同）。</summary>
    public bool TryKeyOfPitch(int pitch, out string key, out int octaveOffset, out bool sharp,
                              out int soundingPitch)
    {
        key = "";
        octaveOffset = 0;
        sharp = false;
        soundingPitch = pitch;
        if (Keys.Count == 0) return false;
        if (!InRange(pitch)) return false;   // 超出能弹范围：固定不弹

        int want = pitch;
        bool canSharp = !string.IsNullOrWhiteSpace(Sharp);
        bool canUp = !string.IsNullOrWhiteSpace(OctaveUp);
        bool canDown = !string.IsNullOrWhiteSpace(OctaveDown);

        bool found = false;
        int bestPitch = 0;
        int bestSharp = 1;
        int bestOffset = int.MaxValue;
        int bestModAbs = int.MaxValue;
        int bestIndex = int.MaxValue;
        string bestKey = "";
        int bestMod = 0;
        bool bestSharpFlag = false;

        for (int mod = -1; mod <= 1; mod++)
        {
            if (mod < 0 && !canDown) continue;
            if (mod > 0 && !canUp) continue;

            for (int i = 0; i < Keys.Count; i++)
            {
                var kb = Keys[i];
                if (kb == null || string.IsNullOrEmpty(kb.Key)) continue;
                if (KeyCharOf(kb.Key) == '\0') continue;   // 认不出的键名不参与映射

                int basePitch = BaseNote + kb.Offset + 12 * mod;
                int maxS = canSharp ? 1 : 0;
                for (int s = 0; s <= maxS; s++)
                {
                    int p = basePitch + s;
                    if (p < 0 || p > 127) continue;

                    // 缺音处理：跳过 = 只认精确命中；用高/低半音代替 = 只认上/下那一侧的候选
                    bool usable = MissingNote switch
                    {
                        MissingNoteMode.Skip => p == want,
                        MissingNoteMode.Up => p >= want,
                        _ => p <= want,
                    };
                    if (!usable) continue;

                    int modAbs = Math.Abs(mod);
                    bool better;
                    if (!found)
                    {
                        better = true;
                    }
                    else if (p != bestPitch)
                    {
                        // Up 取最低的那个候选，Down 取最高的那个候选
                        better = MissingNote == MissingNoteMode.Up ? p < bestPitch : p > bestPitch;
                    }
                    else
                    {
                        better =
                            s < bestSharp ||
                            (s == bestSharp && kb.Offset < bestOffset) ||
                            (s == bestSharp && kb.Offset == bestOffset && modAbs < bestModAbs) ||
                            (s == bestSharp && kb.Offset == bestOffset && modAbs == bestModAbs && i < bestIndex);
                    }
                    if (!better) continue;

                    found = true;
                    bestPitch = p;
                    bestSharp = s;
                    bestOffset = kb.Offset;
                    bestModAbs = modAbs;
                    bestIndex = i;
                    bestKey = kb.Key;
                    bestMod = mod;
                    bestSharpFlag = s == 1;
                }
            }
        }

        if (!found) return false;

        key = bestKey;
        octaveOffset = bestMod;
        sharp = bestSharpFlag;
        soundingPitch = bestPitch;
        return true;
    }

    /// <summary>复制一份（键表也复制），用于编辑内置预设前的拷贝。</summary>
    public KeymapProfile Clone()
    {
        var copy = new KeymapProfile
        {
            Version = Version,
            Name = Name,
            Author = Author,
            Description = Description,
            BaseNote = BaseNote,
            OctaveUp = OctaveUp,
            OctaveDown = OctaveDown,
            Sharp = Sharp,
            MinNote = MinNote,
            MaxNote = MaxNote,
            OutOfRange = OutOfRange,
            MissingNote = MissingNote,
        };
        foreach (var k in Keys)
        {
            if (k == null) continue;
            copy.Keys.Add(new KeyBinding { Key = k.Key, Offset = k.Offset });
        }
        return copy;
    }

    // ================= 键名表 =================
    //
    // 键名到字符是「多对一」：单字符键（字母、数字、标点）用自身；
    // 命名键（PageUp、MouseLeft、F1、NumPad3…）用私用区哨兵字符，一个键名一个码位。
    // 这样播放引擎仍可以用 char 传递按键（PlaybackEngine 的 PhysicalEvent.Code），
    // 而 InputSender / MacroExporter 能把哨兵还原成真正的键名。

    private const char NamedKeyBase = '\uE000';

    /// <summary>命名键（非单字符）。下标 + <see cref="NamedKeyBase"/> 就是它的哨兵字符。</summary>
    private static readonly string[] NamedKeys =
    {
        "Space", "Enter", "Tab", "Back", "Escape", "Shift", "Ctrl", "Alt",
        "PageUp", "PageDown", "Home", "End", "Insert", "Delete",
        "Up", "Down", "Left", "Right",
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
        "NumPad0", "NumPad1", "NumPad2", "NumPad3", "NumPad4",
        "NumPad5", "NumPad6", "NumPad7", "NumPad8", "NumPad9",
        "MouseLeft", "MouseRight", "MouseMiddle",
    };

    /// <summary>允许出现在方案里的别名（统一按小写比较）。</summary>
    private static readonly Dictionary<string, string> KeyAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Backspace"] = "Back",
        ["Esc"] = "Escape",
        ["Return"] = "Enter",
        ["PgUp"] = "PageUp",
        ["PgDn"] = "PageDown",
        ["UpArrow"] = "Up",
        ["DownArrow"] = "Down",
        ["LeftArrow"] = "Left",
        ["RightArrow"] = "Right",
        ["LShift"] = "Shift",
        ["RShift"] = "Shift",
        ["LCtrl"] = "Ctrl",
        ["RCtrl"] = "Ctrl",
        ["LAlt"] = "Alt",
        ["RAlt"] = "Alt",
        ["LeftMouse"] = "MouseLeft",
        ["RightMouse"] = "MouseRight",
        ["MiddleMouse"] = "MouseMiddle",
    };

    /// <summary>可单独作为键名的标点（与界面能录入的键一致）。</summary>
    private const string SingleCharKeys = ",.;/'\\[]-=`";

    /// <summary>归一化键名：大小写、别名。认不出返回空串。</summary>
    public static string CanonicalKeyName(string? keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName)) return "";
        string s = keyName.Trim();

        if (s.Length == 1)
        {
            char c = s[0];
            if (c is >= 'a' and <= 'z') return char.ToUpperInvariant(c).ToString();
            if (c is >= 'A' and <= 'Z') return s;
            if (c is >= '0' and <= '9') return s;
            return SingleCharKeys.Contains(c) ? s : "";
        }

        if (KeyAlias.TryGetValue(s, out string? alias)) s = alias;
        foreach (string n in NamedKeys)
            if (string.Equals(n, s, StringComparison.OrdinalIgnoreCase)) return n;
        return "";
    }

    /// <summary>键名是否可用（单字符键或已知命名键）。</summary>
    public static bool IsKnownKeyName(string? keyName) => CanonicalKeyName(keyName).Length > 0;

    /// <summary>
    /// 键名 → 单字符。单字符键返回自身；命名键返回哨兵字符；认不出返回 '\0'。
    /// </summary>
    public static char KeyCharOf(string? keyName)
    {
        string name = CanonicalKeyName(keyName);
        if (name.Length == 0) return '\0';
        if (name.Length == 1) return name[0];
        int i = Array.IndexOf(NamedKeys, name);
        return i < 0 ? '\0' : (char)(NamedKeyBase + i);
    }

    /// <summary>单字符 → 键名。哨兵字符还原成命名键；其余按原样返回。</summary>
    public static string NameOfKeyChar(char c)
    {
        if (c == '\0') return "";
        if (c >= NamedKeyBase && c < NamedKeyBase + NamedKeys.Length) return NamedKeys[c - NamedKeyBase];
        return c.ToString();
    }
}

/// <summary>
/// 方案 JSON 的源生成上下文。读写必须走它，不能用 JsonSerializer 的反射重载：
/// 发布开了裁剪（PublishTrimmed），反射需要的元数据会被裁掉，运行时抛异常。
/// 字段名按设计文档的字段表写成 camelCase（version / name / baseNote / keys / octaveUp …）；
/// 读的时候大小写不敏感，手写 "BaseNote" 也能读进来。
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(KeymapProfile))]
internal sealed partial class KeymapJson : JsonSerializerContext
{
}

/// <summary>
/// <c>outOfRange</c> 写成小写字符串 <c>drop</c> / <c>fold</c>（设计文档的字段表），
/// 读的时候大小写不敏感，也接受 0/1 两个旧数字写法。
/// </summary>
internal sealed class OutOfRangeModeConverter : JsonConverter<OutOfRangeMode>
{
    public override OutOfRangeMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return reader.GetInt32() == 1 ? LegacyFold() : OutOfRangeMode.Drop;
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("outOfRange 只能写 drop 或 fold。");
        string s = reader.GetString() ?? "";
        if (s.Equals("drop", StringComparison.OrdinalIgnoreCase)) return OutOfRangeMode.Drop;
        if (s.Equals("fold", StringComparison.OrdinalIgnoreCase)) return LegacyFold();
        throw new JsonException($"outOfRange 只能写 drop 或 fold，收到「{s}」。");
    }

    /// <summary>老文件的 fold 保留原值，但新版本固定不弹；写一条日志说明。</summary>
    private static OutOfRangeMode LegacyFold()
    {
        LogFile.Append("[键位] outOfRange 旧值 fold 已停用：超出能弹范围的音固定不弹，不再折八度。");
        return OutOfRangeMode.Fold;
    }

    public override void Write(Utf8JsonWriter writer, OutOfRangeMode value, JsonSerializerOptions options)
        => writer.WriteStringValue(value == OutOfRangeMode.Fold ? "fold" : "drop");
}

/// <summary>
/// <c>missingNote</c> 写成小写字符串 <c>skip</c> / <c>up</c> / <c>down</c>，读取规则同
/// <see cref="OutOfRangeModeConverter"/>。老文件的 <c>snap</c> 升级成 <c>up</c>，<c>drop</c> 升级成 <c>skip</c>，
/// 两种情况都写一条日志。
/// </summary>
internal sealed class MissingNoteModeConverter : JsonConverter<MissingNoteMode>
{
    public override MissingNoteMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            // 老的 0 = 就近吸附 → up；1 = 丢弃 → skip
            var legacy = reader.GetInt32() == 1 ? MissingNoteMode.Skip : MissingNoteMode.Up;
            LogFile.Append($"[键位] missingNote 旧数字写法已升级为「{Token(legacy)}」。");
            return legacy;
        }
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("missingNote 只能写 skip、up 或 down。");

        string s = reader.GetString() ?? "";
        if (s.Equals("skip", StringComparison.OrdinalIgnoreCase)) return MissingNoteMode.Skip;
        if (s.Equals("up", StringComparison.OrdinalIgnoreCase)) return MissingNoteMode.Up;
        if (s.Equals("down", StringComparison.OrdinalIgnoreCase)) return MissingNoteMode.Down;
        if (s.Equals("snap", StringComparison.OrdinalIgnoreCase))
        {
            LogFile.Append("[键位] missingNote 旧值 snap 已升级为 up（就近吸附改成用高半音代替）。");
            return MissingNoteMode.Up;
        }
        if (s.Equals("drop", StringComparison.OrdinalIgnoreCase))
        {
            LogFile.Append("[键位] missingNote 旧值 drop 已升级为 skip（丢弃改成跳过这个音）。");
            return MissingNoteMode.Skip;
        }
        throw new JsonException($"missingNote 只能写 skip、up 或 down，收到「{s}」。");
    }

    public override void Write(Utf8JsonWriter writer, MissingNoteMode value, JsonSerializerOptions options)
        => writer.WriteStringValue(Token(value));

    private static string Token(MissingNoteMode value) => value switch
    {
        MissingNoteMode.Skip => "skip",
        MissingNoteMode.Up => "up",
        _ => "down",
    };
}

/// <summary>
/// <c>keys</c> 里每项写成两元素数组 <c>["Z",0]</c>（设计文档的字段表），不是对象。
/// 放在 <see cref="KeyBinding"/> 类型上，源生成器直接按它读写列表元素。
/// </summary>
internal sealed class KeyBindingConverter : JsonConverter<KeyBinding>
{
    public override KeyBinding Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("keys 里每一项要写成 [\"键名\", 半音偏移] 两元素数组。");

        if (!reader.Read() || reader.TokenType != JsonTokenType.String)
            throw new JsonException("keys 里每一项的第一个值要是键名字符串。");
        string name = reader.GetString() ?? "";

        if (!reader.Read())
            throw new JsonException("keys 里的项不完整。");
        int offset;
        if (reader.TokenType == JsonTokenType.Number)
        {
            if (!reader.TryGetInt32(out offset))
                throw new JsonException($"键「{name}」的半音偏移要写整数。");
        }
        else
        {
            throw new JsonException($"键「{name}」的半音偏移要写整数。");
        }

        if (!reader.Read())
            throw new JsonException("keys 里的项不完整。");
        while (reader.TokenType != JsonTokenType.EndArray)
        {
            if (!reader.Read()) throw new JsonException("keys 里的项不完整。");   // 多余项忽略
        }

        return new KeyBinding { Key = name, Offset = offset };
    }

    public override void Write(Utf8JsonWriter writer, KeyBinding value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteStringValue(value.Key);
        writer.WriteNumberValue(value.Offset);
        writer.WriteEndArray();
    }
}
