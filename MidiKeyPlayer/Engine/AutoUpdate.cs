using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace MidiKeyPlayer.Engine;

/// <summary>后台静默检查 GitHub 新版本，有新版时界面提示并可跳转 Release 页。</summary>
public static class AutoUpdate
{
    /// <summary>
    /// 自动更新开关。仓库目前是私有库，未带凭据的检查会返回 404，所以保持关闭；
    /// 仓库转为公开后改成 true 即可。用 static readonly 而不是 const：
    /// const 为 false 时编译器会把后面整段检查代码判成不可达并报 CS0162。
    /// </summary>
    private static readonly bool Enabled = false;

    private const string Owner = "ChickenD233";
    private const string Repo = "midikey-player";

    /// <summary>最新 Release 页面（用于跳转下载）。</summary>
    public static string ReleasesUrl => $"https://github.com/{Owner}/{Repo}/releases";

    /// <summary>允许跳转的地址前缀。只有本仓库的页面才交给系统浏览器打开。</summary>
    private static readonly string[] AllowedUrlPrefixes =
    {
        $"https://github.com/{Owner}/{Repo}/",
    };

    /// <summary>地址是否属于本仓库。不在白名单就退回 <see cref="ReleasesUrl"/>，不交给 shell。</summary>
    private static bool IsAllowedUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        foreach (string prefix in AllowedUrlPrefixes)
            if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// 当前程序版本（如 1.0.7，第三段必须取 Build，第四段是内部版本号）。
    /// 版本号只支持三段数字（<c>x.y.z</c>）。预发布后缀（<c>1.0.0-rc.1</c> 里的 <c>-rc.1</c>）
    /// 不参与比较，会被丢弃，所以它等于 <c>1.0.0</c>。写 tag 时请只用 <c>vX.Y.Z</c>。
    /// </summary>
    public static string CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v == null) return "0.0.0";
            // 未显式指定版本时 Build/Revision 可能是 -1，用 Math.Max 兜底
            return $"{Math.Max(0, v.Major)}.{Math.Max(0, v.Minor)}.{Math.Max(0, v.Build)}";
        }
    }

    public sealed class Result
    {
        public bool HasUpdate { get; init; }
        public string LatestTag { get; init; } = "";
        public string CurrentTag { get; init; } = "";
        public string ReleaseName { get; init; } = "";
        public string ReleaseUrl { get; init; } = "";
        public bool Skipped { get; init; }       // 用户选了"跳过这个版本"
        public string? Error { get; init; }      // 网络失败等（静默处理，不打扰用户）
    }

    /// <summary>查询最新 Release 并与当前版本比较；最新版正好是被跳过的那版则 HasUpdate=false。</summary>
    public static async Task<Result> CheckAsync(string? skippedTag = null,
                                                CancellationToken ct = default)
    {
        if (!Enabled) return new Result { CurrentTag = CurrentVersion };

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MidiKeyPlayer-UpdateCheck");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            string url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new Result { Error = $"HTTP {(int)resp.StatusCode}", CurrentTag = CurrentVersion };

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
                                             .ConfigureAwait(false);
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            string name = root.TryGetProperty("name", out var nm) ? (nm.GetString() ?? "") : "";
            string html = root.TryGetProperty("html_url", out var h) ? (h.GetString() ?? "") : "";

            // 地址白名单：接口返回的 html_url 只认本仓库前缀，其它一律退回固定的 Releases 页，
            // 免得将来换了数据源以后把 file:/ms-*: 之类的地址直接交给 shell。
            if (html.Length > 0 && !IsAllowedUrl(html))
                Persist.LogFile.Append($"[更新] 接口返回的地址不在白名单，改用 Releases 页：{html}");

            bool newer = IsNewer(tag, CurrentVersion);
            return new Result
            {
                HasUpdate = newer,
                LatestTag = tag.TrimStart('v', 'V'),
                CurrentTag = CurrentVersion,
                ReleaseName = name,
                ReleaseUrl = IsAllowedUrl(html) ? html : ReleasesUrl,
                Skipped = newer && !string.IsNullOrEmpty(skippedTag) &&
                          string.Equals(tag.TrimStart('v', 'V'), skippedTag.TrimStart('v', 'V'),
                                        StringComparison.OrdinalIgnoreCase)
            };
        }
        catch (Exception ex)
        {
            // 没网 / 被墙 / 超时都属正常，静默忽略
            return new Result { Error = ex.GetType().Name, CurrentTag = CurrentVersion };
        }
    }

    /// <summary>版本号比较：latest 是否比 current 新（按 x.y.z 逐段数值比较）。</summary>
    public static bool IsNewer(string latest, string current)
    {
        var a = Parse(latest);
        var b = Parse(current);
        for (int i = 0; i < 3; i++)
        {
            if (a[i] != b[i]) return a[i] > b[i];
        }
        return false;
    }

    private static int[] Parse(string v)
    {
        var parts = (v ?? "").Trim().TrimStart('v', 'V').Split('.', '-', '+');
        var outv = new int[3];
        for (int i = 0; i < parts.Length; i++)
        {
            // 只比较前三段。预发布段（1.0.0-rc.1 里的 rc）被丢掉，所以它等于 1.0.0。
            if (!int.TryParse(parts[i], out int n))
            {
                Persist.LogFile.Append($"[更新] 版本号「{v}」里的「{parts[i]}」不是数字，这一段按 0 算。");
                n = 0;                                  // 预发布段不参与比较
            }
            if (i < 3) outv[i] = n;
        }
        return outv;
    }

    /// <summary>用系统默认浏览器打开链接（跳转到 Release 页面下载）。</summary>
    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // 打不开就算了，界面上也会把链接文字显示出来供手动复制；失败原因写日志
            Persist.LogFile.Append($"[更新] 打开链接失败（{url}）：{ex.Message}");
        }
    }
}
