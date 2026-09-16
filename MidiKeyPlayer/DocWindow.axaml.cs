using Avalonia.Controls;

namespace MidiKeyPlayer;

/// <summary>只读文档查看窗：显示嵌在 exe 里的合规文本（更新说明 / 第三方声明 / 许可证）。</summary>
public partial class DocWindow : Window
{
    public DocWindow()
    {
        InitializeComponent();
    }

    /// <summary>设置标题与正文后显示（Owner 居中）。</summary>
    public void ShowDoc(Window owner, string title, string content)
    {
        Title = title;
        TxtDoc.Text = content;
        TxtDoc.CaretIndex = 0;   // 打开时滚回顶部
        Show(owner);
    }

    private void Close_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
