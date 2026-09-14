using System.Text.Json;
using System.Text.Json.Serialization;
using MidiKeyPlayer.Persist;

namespace MidiKeyPlayer.Engine;

/// <summary>超界处理：丢音 / 就近折八度。JSON 里写 <c>drop</c> / <c>fold</c>。</summary>
[JsonConverter(typeof(OutOfRangeModeConverter))]
public enum OutOfRangeMode { Drop, Fold }

/// <summary>缺音处理：就近吸附 / 丢弃。JSON 里写 <c>snap</c> / <c>drop</c>。</summary>
[JsonConverter(typeof(MissingNoteModeConverter))]
public enum MissingNoteMode { Snap, Drop }

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
/// 键位方案：把「键名 → 半音偏移」与音域、超界策略、缺音策略全部交给配置。
///
/// 活动方案是 <see cref="Current"/>（全局一份）。文件在 %LOCALAPPDATA%\MidiKeyPlayer\keymap.json。
/// 序列化必须走源生成上下文 <see cref="KeymapJson"/>：发布开了 PublishTrimmed，
/// 反射式重载在裁剪下会抛异常。
/// </summary>
public sealed class KeymapProfile
{
    /// <summary>默认方案名。7 个字母键加逗号补高八度 do（旧行为）。</summary>
    public const string DefaultName = "8 键单排（含高八度 do，默认）";

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
    public int? MinNote { get; set; } = 48;
    public int? MaxNote { get; set; } = 85;
    public OutOfRangeMode OutOfRange { get; set; } = OutOfRangeMode.Drop;
    public MissingNoteMode MissingNote { get; set; } = MissingNoteMode.Snap;

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
    /// 6 套内置预设（名字取调研文档的中性名）。第一项就是默认方案 = 旧行为。
    /// 只读用；要改先 <see cref="Clone"/>。
    /// </summary>
    public static IReadOnlyList<KeymapProfile> Presets => _presets ??= BuildPresets();

    /// <summary>默认方案（第 1 节示例值 = 旧行为）。每次取都是新副本，改它不影响内置预设。</summary>
    public static KeymapProfile Default => BuildDefault();

    /// <summary>
    /// 默认方案（也是第 1 套预设）：Z X C V B N M 与逗号；音域 48..85；超界丢音、缺音吸附。
    /// 每次取都是新副本，改它不影响内置预设。
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
        MinNote = 48,
        MaxNote = 85,
        OutOfRange = OutOfRangeMode.Drop,
        MissingNote = MissingNoteMode.Snap,
    };

    /// <summary>第 2 套：只要 7 个字母键，一个八度自然音，没有功能键。</summary>
    private static KeymapProfile BuildPreset7() => new()
    {
        Version = CurrentVersion,
        Name = "7 键单排自然音阶",
        Description = "Z X C V B N M = do..si，一个八度自然音；没有八度键与升半音键，音域 60..71",
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
        MinNote = 60,
        MaxNote = 71,
        OutOfRange = OutOfRangeMode.Drop,
        MissingNote = MissingNoteMode.Snap,
    };

    private static KeymapProfile BuildPreset15() => new()
    {
        Version = CurrentVersion,
        Name = "15 键三排",
        Description = "三排各 5 键，覆盖两个八度自然音，没有功能键",
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
        MinNote = 60,
        MaxNote = 79,
        OutOfRange = OutOfRangeMode.Fold,
        MissingNote = MissingNoteMode.Snap,
    };

    private static KeymapProfile BuildPreset12() => new()
    {
        Version = CurrentVersion,
        Name = "12 键半音阶双排",
        Description = "两排各 6 键，12 个半音一键一音，O 升八度、L 降八度",
        BaseNote = 60,
        Keys = new List<KeyBinding>
        {
            new() { Key = "A", Offset = 0 },
            new() { Key = "W", Offset = 1 },
            new() { Key = "S", Offset = 2 },
            new() { Key = "E", Offset = 3 },
            new() { Key = "D", Offset = 4 },
            new() { Key = "F", Offset = 5 },
            new() { Key = "T", Offset = 6 },
            new() { Key = "G", Offset = 7 },
            new() { Key = "Y", Offset = 8 },
            new() { Key = "H", Offset = 9 },
            new() { Key = "U", Offset = 10 },
            new() { Key = "J", Offset = 11 },
        },
        OctaveUp = "O",
        OctaveDown = "L",
        Sharp = null,
        MinNote = 48,
        MaxNote = 83,
        OutOfRange = OutOfRangeMode.Fold,
        MissingNote = MissingNoteMode.Drop,
    };

    private static KeymapProfile BuildPreset5() => new()
    {
        Version = CurrentVersion,
        Name = "5 键极简",
        Description = "5 键，音域一个八度，缺音就近吸附；适合只弹主旋律",
        BaseNote = 60,
        Keys = new List<KeyBinding>
        {
            new() { Key = "1", Offset = 0 },
            new() { Key = "2", Offset = 2 },
            new() { Key = "3", Offset = 4 },
            new() { Key = "4", Offset = 7 },
            new() { Key = "5", Offset = 12 },
        },
        OctaveUp = null,
        OctaveDown = null,
        Sharp = null,
        MinNote = 60,
        MaxNote = 72,
        OutOfRange = OutOfRangeMode.Fold,
        MissingNote = MissingNoteMode.Snap,
    };

    private static KeymapProfile BuildPresetWide() => new()
    {
        Version = CurrentVersion,
        Name = "宽音域 8 八度折叠",
        Description = "36 键一键一音覆盖 5 个八度；PageUp/PageDown 移八度、Shift 升半音；超界折八度",
        BaseNote = 36,
        Keys = new List<KeyBinding>
        {
            new() { Key = "1", Offset = 0 },
            new() { Key = "2", Offset = 1 },
            new() { Key = "3", Offset = 2 },
            new() { Key = "4", Offset = 3 },
            new() { Key = "5", Offset = 4 },
            new() { Key = "6", Offset = 5 },
            new() { Key = "7", Offset = 6 },
            new() { Key = "8", Offset = 7 },
            new() { Key = "9", Offset = 8 },
            new() { Key = "0", Offset = 9 },
            new() { Key = "Q", Offset = 12 },
            new() { Key = "W", Offset = 13 },
            new() { Key = "E", Offset = 14 },
            new() { Key = "R", Offset = 15 },
            new() { Key = "T", Offset = 16 },
            new() { Key = "Y", Offset = 17 },
            new() { Key = "U", Offset = 18 },
            new() { Key = "I", Offset = 19 },
            new() { Key = "O", Offset = 20 },
            new() { Key = "P", Offset = 21 },
            new() { Key = "A", Offset = 24 },
            new() { Key = "S", Offset = 25 },
            new() { Key = "D", Offset = 26 },
            new() { Key = "F", Offset = 27 },
            new() { Key = "G", Offset = 28 },
            new() { Key = "H", Offset = 29 },
            new() { Key = "J", Offset = 30 },
            new() { Key = "K", Offset = 31 },
            new() { Key = "L", Offset = 32 },
            new() { Key = "Z", Offset = 36 },
            new() { Key = "X", Offset = 37 },
            new() { Key = "C", Offset = 38 },
            new() { Key = "V", Offset = 39 },
            new() { Key = "B", Offset = 40 },
            new() { Key = "N", Offset = 41 },
            new() { Key = "M", Offset = 42 },
        },
        OctaveUp = "PageUp",
        OctaveDown = "PageDown",
        Sharp = "Shift",
        MinNote = 36,
        MaxNote = 96,
        OutOfRange = OutOfRangeMode.Fold,
        MissingNote = MissingNoteMode.Snap,
    };

    private static IReadOnlyList<KeymapProfile> BuildPresets() => new List<KeymapProfile>
    {
        BuildDefault(),      // 第 1 套 = 默认方案（旧行为）
        BuildPreset7(),
        BuildPreset15(),
        BuildPreset12(),
        BuildPreset5(),
        BuildPresetWide(),
    };

    /// <summary>按方案名找内置预设。找不到返回 null。</summary>
    public static KeymapProfile? PresetByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (var p in Presets)
            if (string.Equals(p.Name, name, StringComparison.Ordinal)) return p;
        return null;
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
    /// 全部可用方案名：内置 6 套在前（按 Presets 的顺序），用户方案文件在后（按名字排序）。
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
    /// 按名字载入一个方案。内置名走内置预设，其余读 schemes\&lt;名字&gt;.json。
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
    /// 任何失败都只写日志，返回默认方案的副本，绝不抛异常。
    /// </summary>
    public static KeymapProfile Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = FromJson(File.ReadAllText(FilePath));
                if (!string.IsNullOrWhiteSpace(loaded.Name)) return loaded;
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
        if (profile.MinNote.HasValue) profile.MinNote = Math.Clamp(profile.MinNote.Value, 0, 127);
        if (profile.MaxNote.HasValue) profile.MaxNote = Math.Clamp(profile.MaxNote.Value, 0, 127);
        if (profile.ResolveMinNote() >= profile.ResolveMaxNote())
            throw new KeymapFormatException("方案的音域下限不小于上限。");
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

    // ================= 音域 =================

    /// <summary>基准音所在的八度编号（C4 = 4）。</summary>
    public int BaseOctave => BaseNote / 12 - 1;

    /// <summary>可演奏最低音。MinNote 为空时取 BaseNote + 最小偏移。</summary>
    public int ResolveMinNote()
    {
        if (MinNote.HasValue) return Math.Clamp(MinNote.Value, 0, 127);
        if (!TryOffsetRange(out int min, out _)) return Math.Clamp(BaseNote, 0, 127);
        return Math.Clamp(BaseNote + min, 0, 127);
    }

    /// <summary>可演奏最高音。MaxNote 为空时取 BaseNote + 最大偏移。</summary>
    public int ResolveMaxNote()
    {
        if (MaxNote.HasValue) return Math.Clamp(MaxNote.Value, 0, 127);
        if (!TryOffsetRange(out _, out int max)) return Math.Clamp(BaseNote, 0, 127);
        return Math.Clamp(BaseNote + max, 0, 127);
    }

    private bool TryOffsetRange(out int min, out int max)
    {
        min = 0;
        max = 0;
        bool any = false;
        foreach (var k in Keys)
        {
            if (k == null) continue;
            if (!any) { min = max = k.Offset; any = true; continue; }
            if (k.Offset < min) min = k.Offset;
            if (k.Offset > max) max = k.Offset;
        }
        return any;
    }

    /// <summary>该音高是否在方案声明的音域内（含端点）。</summary>
    public bool InRange(int pitch)
    {
        int lo = ResolveMinNote();
        int hi = ResolveMaxNote();
        if (lo > hi) (lo, hi) = (hi, lo);
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
    /// 顺序：先按 <see cref="OutOfRange"/> 处理超界（丢音 → false；折八度 → 折回音域）；
    /// 再在键表里找候选：候选 = BaseNote + 键偏移 + 12×八度档（+ 升半音键则再 +1）。
    /// 精确命中就用它；没有精确命中时按 <see cref="MissingNote"/> 处理：
    /// Snap = 吸附到最近的候选音，Drop = 返回 false。
    ///
    /// 候选相同时的优先顺序固定：距离近 → 不用升半音键 → 键偏移小 → 八度档绝对值小 → 键表里靠前。
    /// 这套顺序让默认方案与旧版硬编码行为完全一致。
    /// </summary>
    public bool TryKeyOfPitch(int pitch, out string key, out int octaveOffset, out bool sharp)
        => TryKeyOfPitch(pitch, out key, out octaveOffset, out sharp, out _);

    /// <summary>同上，另给出实际发声音高（吸附/折八度后可能与入参不同）。</summary>
    public bool TryKeyOfPitch(int pitch, out string key, out int octaveOffset, out bool sharp,
                              out int soundingPitch)
    {
        key = "";
        octaveOffset = 0;
        sharp = false;
        soundingPitch = pitch;
        if (Keys.Count == 0) return false;

        int want = pitch;
        if (!InRange(want))
        {
            if (OutOfRange == OutOfRangeMode.Drop) return false;
            want = FoldIntoRange(want);
            if (!InRange(want)) return false;
        }

        bool canSharp = !string.IsNullOrWhiteSpace(Sharp);
        bool canUp = !string.IsNullOrWhiteSpace(OctaveUp);
        bool canDown = !string.IsNullOrWhiteSpace(OctaveDown);

        bool found = false;
        int bestDist = int.MaxValue;
        int bestSharp = 1;
        int bestOffset = int.MaxValue;
        int bestModAbs = int.MaxValue;
        int bestIndex = int.MaxValue;
        string bestKey = "";
        int bestMod = 0;
        bool bestSharpFlag = false;
        int bestSounding = want;

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

                    int dist = Math.Abs(p - want);
                    int modAbs = Math.Abs(mod);
                    bool better =
                        !found ||
                        dist < bestDist ||
                        (dist == bestDist && s < bestSharp) ||
                        (dist == bestDist && s == bestSharp && kb.Offset < bestOffset) ||
                        (dist == bestDist && s == bestSharp && kb.Offset == bestOffset && modAbs < bestModAbs) ||
                        (dist == bestDist && s == bestSharp && kb.Offset == bestOffset && modAbs == bestModAbs &&
                         i < bestIndex);
                    if (!better) continue;

                    found = true;
                    bestDist = dist;
                    bestSharp = s;
                    bestOffset = kb.Offset;
                    bestModAbs = modAbs;
                    bestIndex = i;
                    bestKey = kb.Key;
                    bestMod = mod;
                    bestSharpFlag = s == 1;
                    bestSounding = p;
                }
            }
        }

        if (!found) return false;
        if (bestDist > 0 && MissingNote == MissingNoteMode.Drop) return false;

        key = bestKey;
        octaveOffset = bestMod;
        sharp = bestSharpFlag;
        soundingPitch = bestSounding;
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
            return reader.GetInt32() == 1 ? OutOfRangeMode.Fold : OutOfRangeMode.Drop;
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("outOfRange 只能写 drop 或 fold。");
        string s = reader.GetString() ?? "";
        if (s.Equals("drop", StringComparison.OrdinalIgnoreCase)) return OutOfRangeMode.Drop;
        if (s.Equals("fold", StringComparison.OrdinalIgnoreCase)) return OutOfRangeMode.Fold;
        throw new JsonException($"outOfRange 只能写 drop 或 fold，收到「{s}」。");
    }

    public override void Write(Utf8JsonWriter writer, OutOfRangeMode value, JsonSerializerOptions options)
        => writer.WriteStringValue(value == OutOfRangeMode.Fold ? "fold" : "drop");
}

/// <summary>
/// <c>missingNote</c> 写成小写字符串 <c>snap</c> / <c>drop</c>，读取规则同
/// <see cref="OutOfRangeModeConverter"/>。
/// </summary>
internal sealed class MissingNoteModeConverter : JsonConverter<MissingNoteMode>
{
    public override MissingNoteMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return reader.GetInt32() == 1 ? MissingNoteMode.Drop : MissingNoteMode.Snap;
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("missingNote 只能写 snap 或 drop。");
        string s = reader.GetString() ?? "";
        if (s.Equals("snap", StringComparison.OrdinalIgnoreCase)) return MissingNoteMode.Snap;
        if (s.Equals("drop", StringComparison.OrdinalIgnoreCase)) return MissingNoteMode.Drop;
        throw new JsonException($"missingNote 只能写 snap 或 drop，收到「{s}」。");
    }

    public override void Write(Utf8JsonWriter writer, MissingNoteMode value, JsonSerializerOptions options)
        => writer.WriteStringValue(value == MissingNoteMode.Drop ? "drop" : "snap");
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
