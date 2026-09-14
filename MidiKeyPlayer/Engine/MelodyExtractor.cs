using MidiKeyPlayer.Midi;

namespace MidiKeyPlayer.Engine;

/// <summary>
/// 平滑 skyline 单音提取（调研第 4.2 节的推荐档，也是「和弦开关」关档的唯一规则）。
/// 输入复调音符，输出严格单音线：任意两音区间不重叠、按 Start 升序，可直接接 <see cref="NoteMapper.Map"/>。
///
/// 规则：
/// 1. 按起始时刻分组（<see cref="EPS"/> 容差，防未量化 MIDI 把同一和弦切成多个起始时刻）。
/// 2. 本组只有一个候选 → 两道门全部跳过，直接接受这个音。纯单音旋律没有选择对象，
///    级进与大跳都必须原样保留（调研第 7.3 节 T1：纯单音保留率 100%）。
/// 3. 本组候选 ≥ 2 个 → 先过跳变门 |p - cur| ≤ <see cref="JUMP"/>，再过滞回门 |p - cur| ≥ <see cref="HYST"/>。
/// 4. 两道门把候选清空 → 保持当前音高，把当前音延长到本组起始时刻（不换音）。
/// 5. 候选非空 → 取最高音；并列时取力度大者，再并列取结束晚者。
/// 6. 换音时把上一音的 End 截到本音的 Start：区间不重叠；被「保持」的时段由上一音延续，
///    所以输出是一条连续的旋律线，不会在换音处留空洞。
/// 7. 同音高、起音间隔小于 <see cref="GAP"/> 的并成一个音；时值小于 <see cref="MIN"/> 的丢弃。
///
/// 不做多算法选择器，不做机器学习；音域超界与缺音由键位方案引擎负责，这里不管。
/// </summary>
public static class MelodyExtractor
{
    /// <summary>帧长（秒）。<see cref="MIN"/> 与 <see cref="GAP"/> 都等于 2 帧；本实现按起始组扫描，不逐帧采样。</summary>
    public const double FRAME = 0.02;

    /// <summary>滞回门（半音）：与当前音相差不到它就算「摇摆」，不换音。</summary>
    public const double HYST = 3;

    /// <summary>跳变门（半音）：与当前音相差超过它就算「远处伴奏」，不换音。</summary>
    public const double JUMP = 12;

    /// <summary>最短保留时长（秒，2 帧）：短于此的音丢掉，目标程序来不及采样。</summary>
    public const double MIN = 0.04;

    /// <summary>同音高合并间隔（秒，2 帧）：间隔小于它视为一次按键，并成一个音。</summary>
    public const double GAP = 0.04;

    /// <summary>起始时刻分组容差（秒）：同一和弦的起音参差在这个范围内就并成一组。</summary>
    public const double EPS = 0.025;

    /// <summary>轨道名里的打击乐关键字（不区分大小写）。</summary>
    private static readonly string[] PercussionWords = { "drum", "perc", "打击" };

    /// <summary>
    /// 打击乐判定：MIDI 通道 10（0 基下标 9），或轨道名含 drum / perc / 打击。
    /// 轨道名要由调用方给（<see cref="RawNote"/> 不带轨道名）；拿不到就传空串，此时只按通道判。
    /// </summary>
    public static bool IsPercussion(RawNote note, string trackName)
    {
        if (note != null && note.Channel == 9) return true;
        if (string.IsNullOrEmpty(trackName)) return false;

        for (int i = 0; i < PercussionWords.Length; i++)
        {
            if (trackName.Contains(PercussionWords[i], StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// 提取单音线。excludePercussion 为 true 时先排除打击乐（通道 10）。
    /// 输出按 Start 升序、区间互不重叠；<see cref="RawNote.Channel"/> 一律写 -1（线内已混合来源）。
    /// </summary>
    public static List<RawNote> Extract(IReadOnlyList<RawNote> notes, bool excludePercussion = true)
    {
        var result = new List<RawNote>();
        if (notes == null || notes.Count == 0) return result;

        // —— 预处理：排除打击乐，按起始时刻升序（同一时刻按音高升序，保证结果可复现）——
        var src = new List<RawNote>(notes.Count);
        for (int k = 0; k < notes.Count; k++)
        {
            var n = notes[k];
            if (n == null) continue;
            if (excludePercussion && IsPercussion(n, "")) continue;
            src.Add(n);
        }
        if (src.Count == 0) return result;

        src.Sort(static (a, b) =>
        {
            int c = a.Start.CompareTo(b.Start);
            return c != 0 ? c : a.Pitch.CompareTo(b.Pitch);
        });

        // —— 平滑 skyline：逐起始组单遍扫描 ——
        var line = new List<Draft>();
        int cur = -1;                     // 当前旋律音高；-1 = 尚未起音
        double curStart = 0;              // 当前音的谱面起始
        double curEnd = 0;                // 当前音的结束（可被「延长」推后）
        int curVel = 0;

        int i = 0;
        while (i < src.Count)
        {
            double groupStart = src[i].Start;
            int j = i;
            while (j + 1 < src.Count && src[j + 1].Start - groupStart <= EPS) j++;

            RawNote pick;
            if (cur < 0 || j == i)
            {
                // 尚未起音，或本组只有一个候选：两道门全部跳过，直接接受这个音。
                // 纯单音旋律没有选择对象 —— 级进与大于 JUMP 的大跳都必须原样保留。
                pick = src[i];
                for (int k = i + 1; k <= j; k++)
                    if (Better(src[k], pick)) pick = src[k];
            }
            else
            {
                // ① 跳变门：|p - cur| ≤ JUMP；② 滞回门：|p - cur| ≥ HYST
                RawNote? inWindow = null;
                RawNote? far = null;
                for (int k = i; k <= j; k++)
                {
                    double d = Math.Abs(src[k].Pitch - cur);
                    if (d > JUMP) continue;
                    if (Better(src[k], inWindow)) inWindow = src[k];
                    if (d < HYST) continue;
                    if (Better(src[k], far)) far = src[k];
                }

                if (inWindow == null || far == null)
                {
                    // 某一门把候选清空：保持当前音高，把当前音延长到本组起始时刻
                    if (groupStart > curEnd) curEnd = groupStart;
                    i = j + 1;
                    continue;
                }
                pick = far;
            }

            if (cur >= 0 && pick.Pitch == cur && groupStart - curStart < GAP)
            {
                // 同音高、起音离得太近：是同一个按键的重复触发（未量化 MIDI 把一个音切成两次），
                // 并成一个音，不重新按下。真正的重复音（起音间隔 ≥ GAP）会走下面的新音分支。
                if (pick.End > curEnd) curEnd = pick.End;
                i = j + 1;
                continue;
            }

            // 新音开始前，把上一音的 End 截到本音的 Start：区间不重叠，且被「保持」的那一段
            // （本组候选没过门时不再另起新音）由这里连起来，旋律线不会出现空洞。
            if (cur >= 0) line.Add(new Draft(cur, curStart, groupStart, curVel));

            cur = pick.Pitch;
            curStart = groupStart;
            curEnd = Math.Max(pick.End, groupStart);
            curVel = pick.Velocity;
            i = j + 1;
        }
        if (cur >= 0 && curEnd > curStart) line.Add(new Draft(cur, curStart, curEnd, curVel));

        // —— 同音高起音间隔小于 GAP 的合并 → 丢短音 → 再合并一次 ——
        // 用「起音间隔」而不是「尾音到起音的空隙」：连奏的重复音（前音 End = 后音 Start）
        // 是两次真实按键，必须保留；只有起音挤在 GAP 内的才是同一次按键被切开。
        var merged = MergeSamePitch(line);
        var kept = new List<Draft>(merged.Count);
        for (int k = 0; k < merged.Count; k++)
        {
            var d = merged[k];
            if (d.End - d.Start < MIN) continue;      // 时值小于 MIN 的丢弃
            kept.Add(d);
        }
        merged = MergeSamePitch(kept);

        for (int k = 0; k < merged.Count; k++)
        {
            var d = merged[k];
            result.Add(new RawNote
            {
                Pitch = d.Pitch,
                Start = d.Start,
                End = d.End,
                Velocity = d.Velocity,
                Channel = -1
            });
        }
        return result;
    }

    /// <summary>相邻同音高、起音间隔小于 <see cref="GAP"/> 的并成一个音（End 取较晚者，力度取较大者）。</summary>
    private static List<Draft> MergeSamePitch(List<Draft> line)
    {
        var merged = new List<Draft>(line.Count);
        for (int k = 0; k < line.Count; k++)
        {
            var d = line[k];
            if (merged.Count > 0)
            {
                var prev = merged[^1];
                if (prev.Pitch == d.Pitch && d.Start - prev.Start < GAP)
                {
                    if (d.End > prev.End) prev.End = d.End;
                    if (d.Velocity > prev.Velocity) prev.Velocity = d.Velocity;
                    continue;
                }
            }
            merged.Add(new Draft(d.Pitch, d.Start, d.End, d.Velocity));
        }
        return merged;
    }

    /// <summary>组装期的可变音符草稿（<see cref="RawNote"/> 的属性是 init，只能在输出时定稿）。</summary>
    private sealed class Draft
    {
        public int Pitch;
        public double Start;
        public double End;
        public int Velocity;

        public Draft(int pitch, double start, double end, int velocity)
        {
            Pitch = pitch;
            Start = start;
            End = end;
            Velocity = velocity;
        }
    }

    /// <summary>组内选音优先级：音高更高 → 力度更大 → 结束更晚。</summary>
    private static bool Better(RawNote n, RawNote? best)
    {
        if (best == null) return true;
        if (n.Pitch != best.Pitch) return n.Pitch > best.Pitch;
        if (n.Velocity != best.Velocity) return n.Velocity > best.Velocity;
        return n.End > best.End;
    }
}
