using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MidiKeyPlayer.Engine;
using MidiKeyPlayer.Midi;

namespace MidiKeyPlayer;

/// <summary>
/// 键位预览图：自绘一条最多 4 个八度的键盘，把当前键位方案的键位画在上面，一眼核对。
///
/// 颜色约定（与面板上的图例一致）：
///   蓝紫 = 有按键可用的键；
///   橙   = 超出音域、按「折八度」会响；
///   灰   = 超出音域、按「丢音」不响；
///   红   = 音域内却没有按键可用（音域内缺音）。
///
/// 画法（窄控件里也读得清）：
///   键盘只画音域所在的那几个八度，音域窄时键就宽，标签才放得下；
///   键帽文字只给「方案里真的有按键」的白键，字号小、相邻标签交替上下排，避免压字；
///   黑键与没有按键的半音只画色块不写字，键面颜色说明一切。
///   底部说明分三行排：音域 / 两条策略 / 超出可见范围的键数，各自一行，永不重叠。
///
/// 只画图，不改键位引擎的行为；改键位后由面板重新调 SetProfile 刷新。
/// </summary>
public sealed class KeymapKeyboardPreview : Control
{
    // —— 画布：最多 4 个八度；实际画哪几段由音域决定 ——
    private const int MaxOctaves = 4;
    private const double DrawH = 78;
    private const double RowGap = 2;
    private const double NoteRowH = 13;    // 音名行
    private const double InfoRowH = 14;    // 两行说明
    private const double PadSide = 3;

    /// <summary>一个八度里 7 个自然音在八度内的半音位置。</summary>
    private static readonly int[] Natural = { 0, 2, 4, 5, 7, 9, 11 };

    /// <summary>黑键：在它左边那根白键的序号（八度内 0 起）。</summary>
    private static readonly (int WhiteIdx, int Pc)[] Black =
        { (0, 1), (1, 3), (3, 6), (4, 8), (5, 10) };

    private static readonly IBrush WhiteFill = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush WhiteEdge = new SolidColorBrush(Color.Parse("#C9D3DF"));
    private static readonly IBrush BlackFill = new SolidColorBrush(Color.Parse("#3A4757"));
    private static readonly IBrush OctaveLine = new SolidColorBrush(Color.Parse("#8A93A0"));
    private static readonly IBrush OkFill = new SolidColorBrush(Color.Parse("#5B6CF0"));
    private static readonly IBrush FoldFill = new SolidColorBrush(Color.Parse("#B25A00"));
    private static readonly IBrush DropFill = new SolidColorBrush(Color.Parse("#A9B3C0"));
    private static readonly IBrush GapFill = new SolidColorBrush(Color.Parse("#C0392B"));
    private static readonly IBrush OnKeyText = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush MutedText = new SolidColorBrush(Color.Parse("#5B6673"));
    private static readonly IBrush DangerText = new SolidColorBrush(Color.Parse("#A32B22"));
    private static readonly IPen WhitePen = new Pen(WhiteEdge, 1);
    private static readonly IPen OctavePen = new Pen(OctaveLine, 1);
    private static readonly Typeface Face = Typeface.Default;

    private KeymapProfile? _profile;

    public KeymapKeyboardPreview()
    {
        ClipToBounds = true;
    }

    /// <summary>换成新的键位方案重画。传 null 就画一条空键盘。</summary>
    public void SetProfile(KeymapProfile? profile)
    {
        _profile = profile;
        InvalidateVisual();
    }

    private static FormattedText Text(string s, double size, IBrush brush)
        => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush);

    /// <summary>高度按「键盘 + 音名行 + 两行说明」算，宽度给多少用多少（不超过父容器）。</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 380 : availableSize.Width;
        return new Size(Math.Max(220, w), DrawH + NoteRowH + InfoRowH * 2 + RowGap * 3);
    }

    /// <summary>
    /// 音域覆盖到哪几个八度：以「真的有按键」的八度为核心，再往上补到最高那个有按键的音，
    /// 最多 4 个八度。核心八度必须画（基准八度就在那里），否则会画成一片「无键」的灰。
    /// </summary>
    private static (int Lo, int Hi) PitchWindow(int minNote, int maxNote, Dictionary<int, string> covered)
    {
        int minP = Math.Clamp(Math.Min(minNote, maxNote), 0, 127);
        int maxP = Math.Clamp(Math.Max(minNote, maxNote), 0, 127);
        int coreLo = Math.Clamp(minP / 12, 0, 9);
        int coreHi = Math.Clamp(maxP / 12, 0, 9);

        int topKey = coreLo;
        int botKey = coreHi;
        foreach (int p in covered.Keys)
        {
            int oct = Math.Clamp(p / 12, 0, 9);
            if (oct > topKey) topKey = oct;
            if (oct < botKey) botKey = oct;
        }

        // 核心八度之外，只补「真的有按键」的那一格；上下各最多补一格
        int baseHigh = Math.Max(coreHi, topKey);
        int baseLow = Math.Min(coreLo, botKey);
        int hi = Math.Min(9, baseHigh + 1);
        int lo = Math.Max(0, baseLow - 1);

        // 压到 4 个八度：先砍低端，保证基准八度与高处的按键还在
        while (hi - lo + 1 > MaxOctaves && lo < baseLow) lo++;
        while (hi - lo + 1 > MaxOctaves && hi > baseHigh) hi--;
        if (hi - lo + 1 > MaxOctaves) hi = Math.Min(9, lo + MaxOctaves - 1);

        int first = lo * 12;
        int last = hi == 9 ? 127 : (hi + 1) * 12 - 1;
        if (first < 0) first = 0;
        if (last > 127) last = 127;
        if (last <= first) last = Math.Min(127, first + 11);
        return (first, last);
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width;
        if (w < 140) return;

        var profile = _profile ?? KeymapProfile.Default;
        int minNote = profile.ResolveMinNote();
        int maxNote = profile.ResolveMaxNote();
        var covered = CoveredPitches(profile, out bool foldMode, out int lowOutside, out int highOutside);

        var (loPitch, hiPitch) = PitchWindow(minNote, maxNote, covered);
        int octaves = Math.Max(1, Math.Min(MaxOctaves, hiPitch / 12 - loPitch / 12 + 1));

        double kbLeft = PadSide;
        double kbW = Math.Max(40, w - PadSide * 2);
        double whiteW = kbW / (7.0 * octaves);
        double top = 0;
        double bottom = top + DrawH;
        double blackH = DrawH * 0.62;

        // 1) 白键：只画底色；键名留到黑键画完再写，否则会被黑键盖住
        for (int oct = 0; oct < octaves; oct++)
        {
            for (int i = 0; i < 7; i++)
            {
                int pitch = loPitch + oct * 12 + Natural[i];
                double x = kbLeft + (oct * 7 + i) * whiteW;
                var rect = new Rect(x, top, whiteW - 0.6, DrawH);

                if (covered.ContainsKey(pitch))
                {
                    ctx.FillRectangle(OkFill, rect);
                }
                else if (pitch >= minNote && pitch <= maxNote)
                {
                    ctx.FillRectangle(GapFill, rect);            // 音域内却没有按键
                }
                else
                {
                    ctx.FillRectangle(WhiteFill, rect);
                    ctx.FillRectangle(foldMode ? FoldFill : DropFill,
                                      new Rect(x, top + DrawH - 5, whiteW - 0.6, 5));
                }
                ctx.DrawRectangle(null, WhitePen, rect);
            }
        }

        // 2) 黑键：窄键不写字，只画色块，颜色说明一切
        for (int oct = 0; oct < octaves; oct++)
        {
            foreach (var (whiteIdx, pc) in Black)
            {
                int pitch = loPitch + oct * 12 + pc;
                double cx = kbLeft + (oct * 7 + whiteIdx + 1) * whiteW;
                double bw = Math.Max(3, whiteW * 0.6);
                var rect = new Rect(cx - bw / 2, top, bw, blackH);

                if (covered.TryGetValue(pitch, out string? keyName)
                    && !string.IsNullOrEmpty(keyName) && whiteW >= 30)
                {
                    // 键够宽时才给黑键写小字，避免糊成一团
                    ctx.FillRectangle(OkFill, rect);
                    var ft = Text(keyName, 8, OnKeyText);
                    if (ft.Width <= bw - 1)
                        ctx.DrawText(ft, new Point(cx - ft.Width / 2, top + 2));
                }
                else
                {
                    bool inRange = pitch >= minNote && pitch <= maxNote;
                    var fill = covered.ContainsKey(pitch) ? OkFill : (inRange ? GapFill : BlackFill);
                    ctx.FillRectangle(fill, rect);
                    if (fill == BlackFill && !covered.ContainsKey(pitch) && !inRange)
                    {
                        ctx.FillRectangle(foldMode ? FoldFill : DropFill,
                                          new Rect(rect.X, rect.Bottom - 5, rect.Width, 5));
                    }
                }
            }
        }

        // 3) 白键键名：只给真的有按键的键写，字号按键宽算；放不下就不写（颜色仍然标出来）
        for (int oct = 0; oct < octaves; oct++)
        {
            for (int i = 0; i < 7; i++)
            {
                int pitch = loPitch + oct * 12 + Natural[i];
                if (!covered.TryGetValue(pitch, out string? keyName) || string.IsNullOrEmpty(keyName)) continue;
                if (whiteW < 10) continue;

                double x = kbLeft + (oct * 7 + i) * whiteW;
                double size = Math.Clamp(whiteW * 0.62, 7, 10.5);
                var ft = Text(keyName, size, OnKeyText);
                if (ft.Width > whiteW - 1.5) continue;   // 宁可只留颜色，也不要糊字
                ctx.DrawText(ft, new Point(x + (whiteW - ft.Width) / 2, top + 6));
            }
        }

        // 3) 八度分界
        for (int oct = 0; oct <= octaves; oct++)
        {
            double x = kbLeft + oct * 7 * whiteW;
            ctx.DrawLine(OctavePen, new Point(x, top), new Point(x, bottom));
        }

        // 4) 音名行：每个八度的 C 标一次，宽度不够就只写 C
        double y = bottom + RowGap;
        double nameStep = whiteW * 3;
        for (int oct = 0; oct < octaves; oct++)
        {
            double x = kbLeft + oct * 7 * whiteW;
            string name = nameStep >= 26 ? Music.NoteName(loPitch + oct * 12) : "C";
            ctx.DrawText(Text(name, 9, MutedText), new Point(x + 2, y));
        }

        // 5) 说明：一行一条，各自独占一行，永不重叠
        y += NoteRowH;
        ctx.DrawText(Text($"音域 {Music.NoteName(minNote)} ~ {Music.NoteName(maxNote)}"
                          + $"（{maxNote - minNote + 1} 个半音）", 9.5, MutedText),
                     new Point(PadSide, y));
        y += InfoRowH;
        string policy = (foldMode ? "超界：折八度" : "超界：丢音")
                        + "　"
                        + (profile.MissingNote == MissingNoteMode.Snap ? "缺音：就近吸附" : "缺音：丢弃");
        if (lowOutside + highOutside > 0)
            policy += $"　范围外 {lowOutside + highOutside} 键";
        var policyFt = Text(policy, 9.5, lowOutside + highOutside > 0 ? DangerText : MutedText);
        if (policyFt.Width > w - PadSide * 2)
        {
            // 太窄就只留策略，范围外的键数改写进音域行
            policy = foldMode ? "超界：折八度" : "超界：丢音";
            policyFt = Text(policy, 9.5, MutedText);
        }
        ctx.DrawText(policyFt, new Point(PadSide, y));
    }

    /// <summary>
    /// 算出可见键盘上每个音高对应的按键名。
    /// 基准八度内取键位表本身；基准 ±1 八度由「八度上/下键」覆盖，名字带 ↑ / ↓；
    /// 再高一个八度的黑键按「升半音键 + 最近自然音」算出，与映射器的做法一致。
    /// </summary>
    private static Dictionary<int, string> CoveredPitches(KeymapProfile p,
        out bool foldMode, out int lowOutside, out int highOutside)
    {
        foldMode = p.OutOfRange == OutOfRangeMode.Fold;
        lowOutside = 0;
        highOutside = 0;

        // 基准八度内：键位表本身（同一半音号只留第一个按键）
        var baseOctaveKeys = new Dictionary<int, string>();
        foreach (var k in p.Keys ?? new List<KeyBinding>())
        {
            if (k == null || string.IsNullOrEmpty(k.Key)) continue;
            if (k.Offset < 0 || k.Offset > 11) continue;
            int pc = Music.Mod(p.BaseNote + k.Offset, 12);
            if (!baseOctaveKeys.ContainsKey(pc)) baseOctaveKeys[pc] = ShortKey(k.Key);
        }

        int baseOct = p.BaseNote / 12 - 1;
        var map = new Dictionary<int, string>();
        for (int pitch = 0; pitch <= 127; pitch++)
        {
            int oct = pitch / 12 - 1;
            int rel = oct - baseOct;
            if (rel is < -1 or > 1) continue;
            if (!baseOctaveKeys.TryGetValue(Music.Mod(pitch, 12), out string? name)) continue;
            map[pitch] = rel == 0 ? name : name + (rel > 0 ? "↑" : "↓");
        }
        // 最高一个八度档里，只有黑键还能靠「升半音键」够到
        foreach (var (_, pc) in Black)
        {
            for (int pitch = 0; pitch <= 127; pitch++)
            {
                if (Music.Mod(pitch, 12) != pc) continue;
                if (map.ContainsKey(pitch)) continue;
                if (pitch / 12 - 1 - baseOct != 2) continue;
                int natural = Music.Mod(pc - 1, 12);
                if (baseOctaveKeys.TryGetValue(natural, out string? n2)) map[pitch] = n2 + "↑♯";
            }
        }

        // 统计音域外、连八度档也够不到的键（含三个八度档位）
        foreach (var k in p.Keys ?? new List<KeyBinding>())
        {
            if (k == null) continue;
            for (int shift = -1; shift <= 1; shift++)
            {
                int pitch = p.BaseNote + k.Offset + shift * 12;
                if (pitch < 0 || pitch > 127) continue;
                if (pitch < p.ResolveMinNote()) lowOutside++;
                else if (pitch > p.ResolveMaxNote()) highOutside++;
            }
        }
        return map;
    }

    /// <summary>按键名转成键帽上的短名：逗号写成「，」，长名字截到 3 个字符。</summary>
    private static string ShortKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return "?";
        if (key == ",") return "，";
        return key.Length <= 3 ? key : key[..3];
    }
}
