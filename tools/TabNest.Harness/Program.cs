using System.Windows;

namespace TabNest.Harness;

/// <summary>
/// 窗口行为测试宿主。用于可重复验证分组/切换/拆分/恢复，
/// 避免每次都要手动开 IDEA、Chrome 等真实应用。完整实现见任务 #7。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Run(new LauncherWindow());
    }
}

internal sealed class LauncherWindow : Window
{
    public LauncherWindow()
    {
        Title = "TabNest Harness";
        Width = 360;
        Height = 220;
        Content = new System.Windows.Controls.TextBlock
        {
            Text = "测试窗口生成器（待实现）",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }
}
