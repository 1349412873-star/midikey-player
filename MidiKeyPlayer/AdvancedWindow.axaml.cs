using Avalonia.Controls;

namespace MidiKeyPlayer;

/// <summary>
/// 高级设置窗口：设备接入、输入兼容、三个热键、导出按键表、播放前自检。
///
/// 这些控件不是在本窗口里声明的，而是从 <see cref="MainWindow"/> 搬进来的：
/// MainWindow.axaml 里有一块不可见的 AdvancedStash，装着一个 AdvancedBody（StackPanel）。
/// 点主界面的「高级设置…」时，MainWindow 把 AdvancedBody 交给本窗口的 Host，
/// 关窗时再搬回去（见 MainWindow.Advanced_Click / ReturnAdvancedBody）。
///
/// 这样做的好处：这些控件的 x:Name 与事件处理器全留在 MainWindow.axaml，
/// 代码里的引用一行都不用改，也不存在两份状态。代价是本窗口自己没有逻辑，
/// 只负责摆放与关闭。
/// </summary>
public partial class AdvancedWindow : Window
{
    public AdvancedWindow()
    {
        InitializeComponent();
    }

    /// <summary>把从主窗搬来的内容挂上去。重复调用会先摘掉上一份。</summary>
    internal void Attach(Control content)
    {
        Host.Content = null;
        Host.Content = content;
    }

    /// <summary>把内容摘下来，交回主窗（关窗前调用，否则控件还挂在本窗口上会报「已有父级」）。</summary>
    internal Control? Detach()
    {
        Control? content = Host.Content as Control;
        Host.Content = null;
        return content;
    }

    private void Close_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private void Window_Closed(object? sender, System.EventArgs e)
    {
        // 关掉 = 把内容还给主窗。下次再点「高级设置…」会新建一个窗口并再搬一次，
        // 所以这里不做「藏起来复用」，免得窗口实例与控件归属纠缠。
        (Owner as MainWindow)?.ReturnAdvancedBody(this);
    }
}
