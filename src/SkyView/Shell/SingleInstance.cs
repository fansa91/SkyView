using System;
using System.Threading;

namespace SkyView.Shell;

/// <summary>
/// 单实例控制。
/// <para>
/// 用命名互斥量（<c>Local\</c> 前缀 = 当前登录会话）判断是否已有实例。
/// 重复启动时**不弹提示框**，而是把已有窗口叫到前台再退出 ——
/// 用户点了两次图标，期望的是"窗口出现"，不是一个"程序已在运行"的对话框。
/// </para>
/// <para>
/// 另一种做法是 WinAppSDK 的 <c>AppInstance.FindOrRegisterForKey</c>，
/// 但它的 <c>RedirectActivationToAsync</c> 依赖消息泵，在 <see cref="Microsoft.UI.Xaml.Application"/>
/// 构造期同步等待容易死锁，这里选了更可控的互斥量方案。
/// </para>
/// </summary>
internal static class SingleInstance
{
    private const string MutexName = @"Local\SkyView.SingleInstance";

    /// <summary>
    /// 尝试取得实例锁。返回 <c>false</c> 表示已有实例在运行。
    /// 返回的互斥量需要在整个进程生命周期内保持存活（交给字段持有）。
    /// </summary>
    public static bool TryAcquire(out Mutex? mutex)
    {
        try
        {
            mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (createdNew)
                return true;

            mutex.Dispose();
            mutex = null;
            return false;
        }
        catch (AbandonedMutexException)
        {
            // 上一个实例是被强杀的：锁归我们，继续启动
            mutex = new Mutex(initiallyOwned: true, MutexName, out _);
            return true;
        }
        catch
        {
            // 连互斥量都建不出来（极端沙箱环境）时不要拦住用户，让它启动
            mutex = null;
            return true;
        }
    }

    /// <summary>
    /// 把已有实例的主窗口叫到前台。窗口可能还在启动中（尚未 Create），
    /// 所以带一点重试。
    /// </summary>
    public static bool ActivateExisting(int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            var hwnd = FindWindow("WinUIDesktopWin32WindowClass", "SkyView");
            if (hwnd == IntPtr.Zero)
                hwnd = FindWindow(null, "SkyView");

            if (hwnd != IntPtr.Zero)
            {
                // 可能是"关闭到托盘"后的隐藏窗口，先恢复显示再置前
                ShowWindow(hwnd, SW_SHOWNORMAL);
                SetForegroundWindow(hwnd);
                return true;
            }

            Thread.Sleep(120);
        }

        return false;
    }

    private const int SW_SHOWNORMAL = 1;

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
