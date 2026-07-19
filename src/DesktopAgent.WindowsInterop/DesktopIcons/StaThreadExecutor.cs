namespace DesktopAgent.WindowsInterop.DesktopIcons;

/// <summary>
/// 在专用 STA 线程上执行需要 STA 的 COM 操作（如 Shell COM），
/// 避免阻塞 WPF UI 线程和线程池 MTA 线程。
/// </summary>
public sealed class StaThreadExecutor : IDisposable
{
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// 在 STA 线程上执行操作并返回结果。
    /// 每次调用会创建并启动一个新 STA 线程。
    /// </summary>
    /// <typeparam name="T">返回值类型。</typeparam>
    /// <param name="func">需要在 STA 线程上执行的操作。</param>
    /// <returns>操作结果。</returns>
    public Task<T> RunAsync<T>(Func<T> func)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                var result = func();
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "DesktopAgent-STA-Worker",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return tcs.Task;
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
