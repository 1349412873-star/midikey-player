using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace MidiKeyPlayer;

/// <summary>
/// 皮肤（主题）开关：自动 / 浅色（白）/ 深色（黑）。
///
/// 只做一件事：把设置里的档位换成 <see cref="Application.RequestedThemeVariant"/>。
/// 界面上的颜色一律走 <c>DynamicResource</c>（深浅两套写在 Styles\Theme.axaml 的
/// ThemeDictionaries 里），所以换皮肤不用重建窗口，也不用重启。
///
/// 代码里自己画的颜色（卷帘、状态行的 ✔/✘）不走绑定，换皮肤时由
/// <c>MainWindow.OnThemeChanged</c> 重新取一遍。
/// </summary>
internal static class ThemeSwitch
{
    /// <summary>设置文件里的取值：0 自动（跟随系统）、1 浅色、2 深色。</summary>
    public const int Auto = 0;
    public const int Light = 1;
    public const int Dark = 2;

    /// <summary>设置界面上三档的显示名，下标就是取值。</summary>
    public static readonly string[] Names = { "自动（跟随系统）", "浅色（白）", "深色（黑）" };

    /// <summary>把设置里读到的数字夹回 0..2。读到脏值一律当「自动」。</summary>
    public static int Clamp(int mode) => mode is >= Auto and <= Dark ? mode : Auto;

    /// <summary>档位 → Avalonia 主题变体。Default = 跟随系统。</summary>
    public static ThemeVariant VariantOf(int mode) => Clamp(mode) switch
    {
        Light => ThemeVariant.Light,
        Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    /// <summary>按档位应用皮肤。窗口还没建也能调（启动时先设，免得先闪一下白底）。</summary>
    public static void Apply(int mode)
    {
        if (Application.Current is { } app) app.RequestedThemeVariant = VariantOf(mode);
    }

    /// <summary>
    /// 按资源名取主题画刷。**必须带上当前主题变体**：颜色住在 ThemeDictionaries 里，
    /// 不指定变体的查找拿不到它们 —— 拿到 null 的 Foreground 会让键帽文字看不见
    /// （鼠标悬停时 Fluent 模板会换一个底色，字才露出来），所以这条路径只有这一个入口。
    /// 取不到就返回 null，调用方自己决定兜底。
    /// </summary>
    public static IBrush? BrushOf(string key)
    {
        var app = Application.Current;
        if (app == null) return null;
        if (app.TryFindResource(key, app.ActualThemeVariant, out var themed) && themed is IBrush tb)
            return tb;
        // 兜底：万一日后有人把某个键放到主题字典外面（字号那类不分主题的资源就在外面）
        if (app.TryFindResource(key, out var any) && any is IBrush ab)
            return ab;
        return null;
    }
}
