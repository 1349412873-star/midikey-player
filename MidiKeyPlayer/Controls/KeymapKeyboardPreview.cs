using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MidiKeyPlayer.Engine;
using MidiKeyPlayer.Midi;

namespace MidiKeyPlayer;

/// <summary>
/// 键位预览图（竖向）：音高在纵轴、低音在下、高音在上，与卷帘左侧的琴键栏同一朝向，
/// 这样看卷帘和看预览图时音高的方向一致。
///
/// 每个「有按键」的音高行右边写出两块：上排是物理按键名 + 修饰键符号，下排是音名。
/// 同一个物理键在不同八度档下会发出不同音高，所以同一音高可能出现多条，例如：
///   Z        Z↑        Z↓        Z#
/// 符号含义（窄控件里用符号才不会把键名挤掉）：
///   ↑ = 按住升八度键、↓ = 按住降八度键、# = 按住升半音键。
/// 一个音高有多条时按「不加修饰 → 降八度 → 升八度 → 升半音」排序，不同修饰用不同文字色。
///
/// 颜色沿用五种语义（与面板图例一致）：
///   有按键 / 折八度 / 丢音 / 音域内缺键 / 无键。
/// 没有按键的半音只画色块不写字。
///
/// 只画图，不改键位引擎的行为；改键位后由面板重新调 SetProfile 刷新。
/// </summary>
public sealed class KeymapKeyboardPreview : Control
{
    /// <summary>一个八度里 7 个自然音在八度内的半音位置。</summary>
    private static readonly int[] Natural = { 0, 2, 4, 5, 7, 9, 11 };

    private const double PadSide = 3;
    private const double PadTop = 2;
    private const double PadBottom = 14;   // 底部留一行写「窗口外还有多少键」
    private const double MinWhiteW = 13;
    private const double MaxWhiteW = 21;
    private const double KbNoteW = 27;     // 琴键左侧的音名列
    private const double BadgeH = 13;
    private const double BadgeMinW = 15;
    private const double BadgeMaxW = 74;
    private const double PreviewMinH = 340;
    private const double PreviewMaxH = 430;
    private const double MaxSemitonesWindow = 46;

    private static readonly IBrush WhiteFill = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush WhiteEdge = new SolidColorBrush(Color.Parse("#C9D3DF"));
    private static readonly IBrush BlackFill = new SolidColorBrush(Color.Parse("#3A4757"));
    private static readonly IBrush OkFill = new SolidColorBrush(Color.Parse("#5B6CF0"));
    private static readonly IBrush FoldFill = new SolidColorBrush(Color.Parse("#B25A00"));
    private static readonly IBrush DropFill = new SolidColorBrush(Color.Parse("#A9B3C0"));
    private static readonly IBrush GapFill = new SolidColorBrush(Color.Parse("#C0392B"));
    private static readonly IBrush NoteText = new SolidColorBrush(Color.Parse("#2A3646"));
    private static readonly IBrush MutedText = new SolidColorBrush(Color.Parse("#5B6673"));
    private static readonly IBrush DangerText = new SolidColorBrush(Color.Parse("#A32B22"));
    private static readonly IBrush BadgeBg = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush BadgeBaseText = new SolidColorBrush(Color.Parse("#3A4757"));
    private static readonly IBrush BadgeUpText = new SolidColorBrush(Color.Parse("#A34A00"));
    private static readonly IBrush BadgeDownText = new SolidColorBrush(Color.Parse("#1E6F8C"));
    private static readonly IBrush BadgeSharpText = new SolidColorBrush(Color.Parse("#6B3FA0"));
    private static readonly IPen WhitePen = new Pen(WhiteEdge, 1);
    private static readonly IPen BadgePen = new Pen(new SolidColorBrush(Color.Parse("#D3DAE3")), 1);
    private static readonly Typeface Face = Typeface.Default;
    private static readonly Typeface MonoFace = new("Cascadia Mono, Consolas, Menlo, monospace");

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

    private static FormattedText Text(string s, double size, IBrush brush, bool mono = false)
        => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
               mono ? MonoFace : Face, size, brush);

    /// <summary>竖向：宽度按内容算（不超过父容器），高度取窗口的合理高度。</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        double availW = double.IsInfinity(availableSize.Width) ? 170 : availableSize.Width;
        double width = Math.Min(Math.Max(126, availW), KbNoteW + PadSide * 2 + MaxWhiteW * 7 + 130);

        // 父容器给无限高时（Auto 行）取最大高度，让琴键多露几行
        double availH = double.IsInfinity(availableSize.Height) ? PreviewMaxH : availableSize.Height;
        double height = Math.Clamp(availH, PreviewMinH, PreviewMaxH);
        return new Size(width, height);
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w < 90 || h < 80) return;

        var profile = _profile ?? KeymapProfile.Default;
        int minNote = profile.ResolveMinNote();
        int maxNote = profile.ResolveMaxNote();
        var covered = CoveredPitches(profile);

        var (lo, hi, belowOutside, aboveOutside) = PitchWindow(covered, minNote, maxNote);
        double plotH = Math.Max(60, h - PadTop - PadBottom);
        double half = plotH / 14.0;                 // 两个半音 = 一根白键高
        double rowY0 = PadTop + plotH;              // pitch = lo 的 y（低音在下）

        double kbW = Math.Clamp(MinWhiteW * 7, 84, MaxWhiteW * 7);
        double whiteW = kbW / 7.0;
        double blackW = Math.Max(4, whiteW * 0.6);
        double kbLeft = PadSide;

        double YOf(int pitch) => rowY0 - (pitch - lo) * half;

        // 1) 白键：每个八度铺 7 列，有按键的整格上色
        // 1) 白键：每个八度各占 7 列，do 永远在最左一列。
        //    按「音高在八度内的序号」定列，不能按半音数换算，否则每换一个八度琴键就向右漂一格。
        for (int oct = lo / 12; oct <= hi / 12; oct++)
        {
            for (int i = 0; i < 7; i++)
            {
                int p = oct * 12 + Natural[i];
                if (p < lo || p > hi) continue;
                double x = kbLeft + i * whiteW;
                var rect = new Rect(x, YOf(p + 1), whiteW - 0.6, half * 2);

                if (covered.ContainsKey(p))
                    ctx.FillRectangle(OkFill, rect);
                else if (p >= minNote && p <= maxNote)
                    ctx.FillRectangle(GapFill, rect);          // 音域内却没有按键
                else
                    ctx.FillRectangle(WhiteFill, rect);
                ctx.DrawRectangle(null, WhitePen, rect);
            }
        }

        // 2) 黑键：比白键窄，骑在两根白键的交界处（同样按八度内的位置定列）
        for (int oct = lo / 12; oct <= hi / 12; oct++)
        {
            for (int i = 0; i < 7; i++)
            {
                int p = oct * 12 + Natural[i];
                int blackPitch = p - 1;                     // 每根白键左侧的黑键
                if (blackPitch < lo || blackPitch > hi) continue;
                if (BlackIndexOf(Music.Mod(blackPitch, 12)) < 0) continue;
                double x = kbLeft + i * whiteW;             // 交界处 = 第 i 根白键的左边界
                var rect = new Rect(x - blackW / 2, YOf(blackPitch + 1), blackW, half * 2);

                if (covered.ContainsKey(blackPitch))
                    ctx.FillRectangle(OkFill, rect);
                else if (blackPitch >= minNote && blackPitch <= maxNote)
                    ctx.FillRectangle(GapFill, rect);
                else
                    ctx.FillRectangle(BlackFill, rect);
            }
        }

        // 3) 按键名 + 修饰键（上）与音名（下）：只给有按键的音高写，两行都不与邻行重叠
        double labelLeft = kbLeft + kbW + 5;
        double labelW = Math.Max(20, w - labelLeft - PadSide);
        foreach (var kv in covered)
        {
            int pitch = kv.Key;
            if (pitch < lo || pitch > hi) continue;
            var list = kv.Value;
            if (list.Count == 0) continue;

            double cy = YOf(pitch) - half;                  // 该音高行的中心
            var badge = list[0];                            // 一行只写一条：多出来的折叠成 +N
            var ft = Text(badge.Text, 8.5, badge.TextBrush, mono: true);
            double bw = Math.Clamp(ft.Width + 8, BadgeMinW, BadgeMaxW);
            if (bw > labelW) bw = labelW;
            var box = new Rect(labelLeft, cy - BadgeH, bw, BadgeH);
            ctx.DrawRectangle(BadgeBg, BadgePen, box, 3, 3);
            if (ft.Width <= bw - 4)
                ctx.DrawText(ft, new Point(labelLeft + 4, cy - BadgeH + (BadgeH - ft.Height) / 2));

            string noteText = Music.NoteName(pitch);
            if (list.Count > 1) noteText += "  +" + (list.Count - 1);
            var note = Text(noteText, 8.5, MutedText);
            ctx.DrawText(note, new Point(labelLeft + 2, cy - 1));
        }

        // 4) 底部：窗口外还有多少键；没超音域就写音域范围
        var bottom = Text($"音域 {Music.NoteName(minNote)} ~ {Music.NoteName(maxNote)}", 9, MutedText);
        if (belowOutside > 0 || aboveOutside > 0)
        {
            string info = "";
            if (aboveOutside > 0) info = $"高音区还有 {aboveOutside} 键";
            if (belowOutside > 0)
                info += (info.Length > 0 ? "；" : "") + $"低音区还有 {belowOutside} 键";
            info = "窗口外：" + info;
            bottom = Text(info, 9, DangerText);
            if (bottom.Width > w - PadSide * 2)
                bottom = Text($"窗外还有 {aboveOutside + belowOutside} 键", 9, DangerText);
        }
        if (bottom.Width <= w - PadSide * 2)
            ctx.DrawText(bottom, new Point(PadSide, h - bottom.Height));
    }

    /// <summary>自然音在八度内的列号（do=0 … si=6）；不是自然音返回 -1。</summary>
    private static int NaturalOf(int pc) => pc switch
    {
        0 => 0, 2 => 1, 4 => 2, 5 => 3, 7 => 4, 9 => 5, 11 => 6, _ => -1
    };

    /// <summary>黑键左边那根白键的列号；不是黑键返回 -1。</summary>
    private static int BlackIndexOf(int pc) => pc switch
    {
        1 => 0, 3 => 1, 6 => 3, 8 => 4, 10 => 5, _ => -1
    };

    /// <summary>一行里的一条：物理按键名 + 所需修饰键，以及该用哪种文字色。</summary>
    private readonly record struct KeyBadge(string Text, IBrush TextBrush);

    /// <summary>
    /// 每个音高对应的「按键 + 修饰键」清单，与映射器同一套规则：
    /// 候选 = BaseNote + 键偏移 + 12×八度档，升半音键再 +1；精确命中才算。
    /// 超界按方案策略处理：丢音不画，折八度折回音域再画。
    /// </summary>
    private static Dictionary<int, List<KeyBadge>> CoveredPitches(KeymapProfile p)
    {
        var map = new Dictionary<int, List<KeyBadge>>();
        if (p.Keys == null || p.Keys.Count == 0) return map;

        bool fold = p.OutOfRange == OutOfRangeMode.Fold;
        bool canUp = !string.IsNullOrWhiteSpace(p.OctaveUp);
        bool canDown = !string.IsNullOrWhiteSpace(p.OctaveDown);
        bool canSharp = !string.IsNullOrWhiteSpace(p.Sharp);

        var found = new List<(int Pitch, int Order, KeyBadge Badge)>();
        var seen = new HashSet<string>();

        foreach (var k in p.Keys)
        {
            if (k == null || string.IsNullOrEmpty(k.Key)) continue;
            if (KeymapProfile.KeyCharOf(k.Key) == '\0') continue;   // 认不出的键名不参与映射

            for (int mod = -1; mod <= 1; mod++)
            {
                if (mod < 0 && !canDown) continue;
                if (mod > 0 && !canUp) continue;

                for (int sharp = 0; sharp <= (canSharp ? 1 : 0); sharp++)
                {
                    int pitch = p.BaseNote + k.Offset + 12 * mod + sharp;
                    if (pitch < 0 || pitch > 127) continue;

                    if (!p.InRange(pitch))
                    {
                        if (!fold) continue;
                        pitch = p.FoldIntoRange(pitch);
                        if (pitch < 0 || pitch > 127 || !p.InRange(pitch)) continue;
                    }

                    string text = k.Key;
                    IBrush brush = BadgeBaseText;
                    if (sharp == 1)
                    {
                        text += "#";      // # = 按住升半音键
                        brush = BadgeSharpText;
                    }
                    if (mod < 0)
                    {
                        text += "↓";      // ↓ = 按住降八度键
                        brush = sharp == 1 ? BadgeSharpText : BadgeDownText;
                    }
                    else if (mod > 0)
                    {
                        text += "↑";      // ↑ = 按住升八度键
                        brush = sharp == 1 ? BadgeSharpText : BadgeUpText;
                    }

                    if (!seen.Add(pitch + "|" + text)) continue;
                    found.Add((pitch, Math.Abs(mod) * 2 + sharp, new KeyBadge(text, brush)));
                }
            }
        }

        foreach (var f in found)
        {
            if (!map.TryGetValue(f.Pitch, out var list))
            {
                list = new List<KeyBadge>();
                map[f.Pitch] = list;
            }
            list.Add(f.Badge);
        }
        foreach (var list in map.Values)
            list.Sort((a, b) => OrderOf(a.Text).CompareTo(OrderOf(b.Text)));
        return map;
    }

    /// <summary>排序权重：不加修饰 0、降八度 1、升八度 2、升半音 3、两种一起 4。</summary>
    private static int OrderOf(string text)
    {
        bool down = text.Contains('↓');
        bool up = text.Contains('↑');
        bool sharp = text.Contains('#');
        if (sharp && (down || up)) return 4;
        if (sharp) return 3;
        if (down) return 1;
        if (up) return 2;
        return 0;
    }

    /// <summary>
    /// 要画的音高窗口：同时覆盖音域与按键所在音高，最多 46 个半音（约 3 个多八度）。
    /// 超出时按窗口显示，并回传窗口外上/下各有多少个有按键的键。
    /// </summary>
    private static (int Lo, int Hi, int Below, int Above) PitchWindow(
        Dictionary<int, List<KeyBadge>> covered, int minNote, int maxNote)
    {
        int minP = Math.Clamp(Math.Min(minNote, maxNote), 0, 127);
        int maxP = Math.Clamp(Math.Max(minNote, maxNote), 0, 127);

        int low = covered.Count == 0 ? minP : covered.Keys.Min();
        int high = covered.Count == 0 ? maxP : covered.Keys.Max();
        int w = (int)MaxSemitonesWindow;
        int lo = Math.Min(low, minP);
        int hi = Math.Max(high, maxP);
        if (hi < lo) (lo, hi) = (hi, lo);
        if (hi - lo + 1 <= w) return (lo, hi, 0, 0);

        // 以低端为锚点，保证基准八度（低音 do 那一带）在窗口里
        int newHi = lo + w - 1;
        if (newHi > 127) { newHi = 127; lo = Math.Max(0, 127 - w + 1); }
        int below = 0, above = 0;
        foreach (int p in covered.Keys)
        {
            if (p < lo) below++;
            else if (p > newHi) above++;
        }
        return (lo, newHi, below, above);
    }
}
