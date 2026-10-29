using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SkyView.Shell;

/// <summary>
/// 系统托盘图标（纯 Win32 实现，不依赖 WinForms）。
/// <para>
/// 为什么不用 <c>System.Windows.Forms.NotifyIcon</c>：开启 <c>UseWindowsForms</c>
/// 会把 WindowsDesktop SDK 的 WPF markup 编译目标带进来，它会把 WinUI 的 .xaml
/// 当 WPF Page 处理，构建直接失败（MC6000）。为了一个托盘图标重构整个项目不值得。
/// </para>
/// <para>
/// 实现要点：创建一个**消息专用窗口**（parent = HWND_MESSAGE）接收 Shell 的回调消息，
/// 右键菜单用 TrackPopupMenu 自己弹。菜单弹出前必须 SetForegroundWindow，
/// 否则点别处时菜单不会消失（这是 Shell 通知图标的经典坑）。
/// </para>
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int WM_TRAYICON = WM_APP + 1;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const int NIM_ADD = 0;
    private const int NIM_MODIFY = 1;
    private const int NIM_DELETE = 2;

    private const int NIF_MESSAGE = 0x01;
    private const int NIF_ICON = 0x02;
    private const int NIF_TIP = 0x04;

    private const int IMAGE_ICON = 1;
    private const int LR_LOADFROMFILE = 0x0010;

    private const int MF_STRING = 0x0000;
    private const int MF_SEPARATOR = 0x0800;
    private const int TPM_RIGHTBUTTON = 0x0002;
    private const int TPM_RETURNCMD = 0x0100;

    private const int CmdShow = 1;
    private const int CmdRefresh = 2;
    private const int CmdExit = 3;

    /// <summary>
    /// 静态引用：WndProc 是静态委托，必须保证它引用的实例不被 GC 回收，
    /// 否则消息到来时会跳进已释放的对象（表现为进程无征兆退出）。
    /// </summary>
    private static TrayIcon? _current;

    private static WndProcDelegate? _wndProc;   // 委托本身也要保活

    private readonly IntPtr _hwnd;
    private readonly IntPtr _iconHandle;
    private IntPtr _menu;
    private bool _disposed;

    public event Action? ShowRequested;
    public event Action? RefreshRequested;
    public event Action? ExitRequested;

    public TrayIcon(string iconPath)
    {
        _current = this;
        _wndProc = WndProc;

        var module = GetModuleHandle(null);
        var className = "SkyViewTrayWindow";

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = module,
            lpszClassName = className,
        };
        RegisterClassEx(ref wc);

        // HWND_MESSAGE = (HWND)-3，消息专用窗口，不参与布局也不显示
        _hwnd = CreateWindowEx(0, className, "SkyViewTray", 0, 0, 0, 0, 0,
            new IntPtr(-3), IntPtr.Zero, module, IntPtr.Zero);

        _iconHandle = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE);
        _menu = BuildMenu();

        var data = CreateData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = WM_TRAYICON;
        data.hIcon = _iconHandle;
        data.szTip = Truncate(Path.GetFileNameWithoutExtension(iconPath), 127);

        if (!Shell_NotifyIcon(NIM_ADD, ref data))
            throw new InvalidOperationException("注册托盘图标失败（Shell_NotifyIcon 返回 0）。");
    }

    /// <summary>更新悬停提示。<paramref name="text"/> 超过 127 字符会被截断（结构体上限）。</summary>
    public void SetTooltip(string text)
    {
        if (_disposed || _hwnd == IntPtr.Zero)
            return;

        var data = CreateData();
        data.uFlags = NIF_TIP;
        data.szTip = Truncate(text, 127);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private IntPtr BuildMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, MF_STRING, CmdShow, "显示主窗口");
        AppendMenu(menu, MF_STRING, CmdRefresh, "立即刷新");
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, MF_STRING, CmdExit, "退出");
        return menu;
    }

    private NOTIFYICONDATA CreateData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        // ByValTStr 字段留 null 时封送行为依赖运行时实现，显式给空串最稳
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private void ShowMenu()
    {
        if (_menu == IntPtr.Zero)
            return;

        GetCursorPos(out var point);

        // 不先抢前台的话，菜单弹出后点别处不会消失
        SetForegroundWindow(_hwnd);

        var command = TrackPopupMenu(_menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, point.X, point.Y,
            0, _hwnd, IntPtr.Zero);

        // 文档要求：菜单关闭后给窗口投一个空消息，否则第一次点击别处只关菜单、
        // 第二次才真正生效
        PostMessage(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);

        switch (command)
        {
            case CmdShow:
                ShowRequested?.Invoke();
                break;
            case CmdRefresh:
                RefreshRequested?.Invoke();
                break;
            case CmdExit:
                ExitRequested?.Invoke();
                break;
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var self = _current;

        if (self is not null && msg == WM_TRAYICON)
        {
            var mouse = (uint)(lParam.ToInt64() & 0xFFFF);
            switch (mouse)
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    self.ShowRequested?.Invoke();
                    break;
                case WM_RBUTTONUP:
                    self.ShowMenu();
                    break;
            }
            return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static string Truncate(string text, int max)
    {
        var value = text.Trim();
        return value.Length <= max ? value : value[..max];
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        var data = CreateData();
        Shell_NotifyIcon(NIM_DELETE, ref data);

        if (_iconHandle != IntPtr.Zero)
            DestroyIcon(_iconHandle);

        if (_menu != IntPtr.Zero)
        {
            DestroyMenu(_menu);
            _menu = IntPtr.Zero;
        }

        if (_hwnd != IntPtr.Zero)
            DestroyWindow(_hwnd);

        if (ReferenceEquals(_current, this))
            _current = null;
    }

    // ---------------- Win32 ----------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int width, int height,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadImage(IntPtr hinst, string name, int type, int cx, int cy, int load);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, int uFlags, int uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, int uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
