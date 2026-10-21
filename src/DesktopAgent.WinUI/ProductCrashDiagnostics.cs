using System.Text;
using Microsoft.UI.Xaml;

namespace DesktopAgent.WinUI;

/// <summary>
/// 产品壳的进程级异常诊断。
///
/// 背景：WinUI 里 <c>async void</c> 事件处理器逃出去的异常会被 XAML 框架变成 WinRT stowed
/// exception（<c>0xc000027b</c>，故障模块 <c>Microsoft.UI.Xaml.dll</c>）直接 fail-fast：
/// 进程消失、桌面窗口一起没了，而 Windows 事件日志里只有一条 Application Error，
/// 既没有异常类型也没有调用栈。真机上一次「应用到桌面」崩溃就是这样消失的。
///
/// 这里只做一件事：把异常类型、消息、调用栈追加到固定路径的日志文件，
/// 让任何一次产品壳崩溃都留下可读的证据。它不改变任何安全语义——写日志失败会被吞掉，
/// 诊断本身绝不能成为新的故障源。
/// </summary>
internal static class ProductCrashDiagnostics
{
    private static readonly object Gate = new();

    /// <summary>崩溃日志路径（固定、与数据目录产物无关，方便真机取证）。</summary>
    public static string LogPath { get; } =
        Path.Combine(Path.GetTempPath(), "tessel-product-crash.log");

    public static void Install(Microsoft.UI.Xaml.Application application)
    {
        ArgumentNullException.ThrowIfNull(application);

        application.UnhandledException += (_, args) =>
            Write("Application.UnhandledException", args.Exception, args.Handled);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Write("AppDomain.UnhandledException", args.ExceptionObject as Exception, handled: false);

        TaskScheduler.UnobservedTaskException += (_, args) =>
            Write("TaskScheduler.UnobservedTaskException", args.Exception, handled: false);
    }

    public static void Write(string source, Exception? exception, bool handled)
    {
        try
        {
            var builder = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("O"))
                .Append("  [").Append(source).Append(']')
                .Append(" handled=").Append(handled)
                .AppendLine()
                .Append(exception is null ? "(异常对象为 null)" : exception.ToString())
                .AppendLine()
                .AppendLine("------------------------------------------------------------");

            lock (Gate)
            {
                File.AppendAllText(LogPath, builder.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // 诊断写入失败不能影响产品行为。
        }
    }
}
