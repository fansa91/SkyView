"""第一/第二梯队功能的可视验证：启动应用 → 逐页截图 → 切单位对比 → 关闭。

只做取证，不做断言（断言在 xUnit 与 check_ui_pywinauto.py 里）。
用法:
    C:/python/python.exe tools/capture_tiers.py
    C:/python/python.exe tools/capture_tiers.py --app-path <SkyView.exe>
输出:
    docs/shots/12-数据源页.png / 01-天气页.png / 01b-温度趋势.png / 01c-预报与生活指数.png
    docs/shots/02-设置页.png / 03-设置保存后.png（实时保存，无保存按钮）/ 04-切换单位后.png
"""

from __future__ import annotations

import argparse
import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time
from pathlib import Path
from typing import Any

from PIL import Image, ImageGrab


def _enable_dpi_awareness() -> None:
    """必须抢在任何窗口查询/截图之前调用。

    不设的话进程是 DPI-unaware 的，GetWindowRect 返回的是**逻辑像素**，
    而 ImageGrab / SetCursorPos 按物理像素工作。在 125% 缩放的机器上两者差 25%，
    截出来的图会带上窗口外的桌面碎片、鼠标也会落到别的窗口上。
    """
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)  # PROCESS_PER_MONITOR_DPI_AWARE
    except Exception:
        try:
            ctypes.windll.user32.SetProcessDPIAware()
        except Exception:
            pass


_enable_dpi_awareness()

REPO_ROOT = Path(__file__).resolve().parent.parent
SHOTS = REPO_ROOT / "docs" / "shots"

CANDIDATES = [
    REPO_ROOT / "src/SkyView/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/publish/SkyView.exe",
    REPO_ROOT / "src/SkyView/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/SkyView.exe",
]

HWND_TOPMOST = -1
HWND_NOTOPMOST = -2
SWP_NOSIZE = 0x0001
SWP_NOMOVE = 0x0002
SWP_SHOWWINDOW = 0x0040

# PrintWindow 的 flag：不加这个，WinUI/WinAppSDK 这种 DirectComposition 渲染的窗口
# 截出来是纯黑或纯白（走的是 WM_PRINT 那条老路，拿不到合成后的内容）。
PW_RENDERFULLCONTENT = 0x00000002


class _BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wt.DWORD),
        ("biWidth", ctypes.c_long),
        ("biHeight", ctypes.c_long),
        ("biPlanes", wt.WORD),
        ("biBitCount", wt.WORD),
        ("biCompression", wt.DWORD),
        ("biSizeImage", wt.DWORD),
        ("biXPelsPerMeter", ctypes.c_long),
        ("biYPelsPerMeter", ctypes.c_long),
        ("biClrUsed", wt.DWORD),
        ("biClrImportant", wt.DWORD),
    ]


# ---------------------------------------------------------------- 窗口
def _find_exe(override: str | None) -> Path:
    if override:
        return Path(override)
    for candidate in CANDIDATES:
        if candidate.is_file():
            return candidate
    raise SystemExit("找不到 SkyView.exe，先跑 dotnet publish 或用 --app-path 指定")


def _hwnd_of(pid: int) -> int | None:
    """兜底：按进程枚举可见窗口。

    注意不要指望它一次就拿到主窗口——WinAppSDK 应用还有 DesktopWindowXamlSource
    之类的隐藏/辅助窗口，靠 EnumWindows 取第一个可见窗口可能拿到尺寸不对的那个。
    正常路径应该用 pywinauto 的 wrapper.handle。
    """
    user32 = ctypes.windll.user32
    found: list[tuple[int, int]] = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def _enum(hwnd, _lparam):
        owner = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            rect = wt.RECT()
            user32.GetWindowRect(hwnd, ctypes.byref(rect))
            area = (rect.right - rect.left) * (rect.bottom - rect.top)
            found.append((area, hwnd))
        return True

    user32.EnumWindows(_enum, 0)
    return max(found)[1] if found else None


def _wait_main_window(pid: int, timeout: float) -> int:
    """等主窗口出现。按面积取最大的可见窗口，过滤掉 Mode Indicator / IME 这类小窗。"""
    deadline = time.time() + timeout
    while time.time() < deadline:
        hwnd = _hwnd_of(pid)
        if hwnd:
            left, top, right, bottom = _rect(hwnd)
            if (right - left) >= 400 and (bottom - top) >= 300:
                return hwnd
        time.sleep(0.4)
    raise SystemExit(f"{timeout}s 内没等到主窗口")


def _rect(hwnd: int) -> tuple[int, int, int, int]:
    rect = wt.RECT()
    ctypes.windll.user32.GetWindowRect(hwnd, ctypes.byref(rect))
    return rect.left, rect.top, rect.right, rect.bottom


def _capture_printwindow(hwnd: int) -> Image.Image | None:
    """用 PrintWindow 让窗口自己把内容画到内存 DC。

    比 ImageGrab 靠谱的地方：不受窗口位置、遮挡、多显示器负坐标影响，
    也不会把窗口外那一圈桌面（任务栏、别的窗口边角）一起截进来。
    实测 WinAppSDK 3 窗口配合 PW_RENDERFULLCONTENT 能拿到完整画面。

    注意这里所有返回句柄的 GDI 函数都必须显式声明 restype，ctypes 默认按
    c_int 处理，64 位下句柄会被截断成垃圾值。
    """
    user32 = ctypes.windll.user32
    gdi32 = ctypes.windll.gdi32

    user32.GetWindowDC.restype = wt.HDC
    user32.GetWindowDC.argtypes = [wt.HWND]
    user32.ReleaseDC.argtypes = [wt.HWND, wt.HDC]
    user32.PrintWindow.argtypes = [wt.HWND, wt.HDC, wt.UINT]
    gdi32.CreateCompatibleDC.restype = wt.HDC
    gdi32.CreateCompatibleDC.argtypes = [wt.HDC]
    gdi32.CreateCompatibleBitmap.restype = wt.HBITMAP
    gdi32.CreateCompatibleBitmap.argtypes = [wt.HDC, ctypes.c_int, ctypes.c_int]
    gdi32.SelectObject.restype = wt.HGDIOBJ
    gdi32.SelectObject.argtypes = [wt.HDC, wt.HGDIOBJ]
    gdi32.DeleteObject.argtypes = [wt.HGDIOBJ]
    gdi32.DeleteDC.argtypes = [wt.HDC]
    gdi32.GetDIBits.argtypes = [
        wt.HDC, wt.HBITMAP, wt.UINT, wt.UINT,
        ctypes.c_void_p, ctypes.c_void_p, wt.UINT,
    ]

    left, top, right, bottom = _rect(hwnd)
    width, height = right - left, bottom - top
    if width <= 0 or height <= 0:
        return None

    hdc = user32.GetWindowDC(hwnd)
    if not hdc:
        return None
    mdc = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, width, height)
    old = gdi32.SelectObject(mdc, bmp)
    try:
        if not user32.PrintWindow(hwnd, mdc, PW_RENDERFULLCONTENT):
            return None

        header = _BITMAPINFOHEADER()
        header.biSize = ctypes.sizeof(_BITMAPINFOHEADER)
        header.biWidth = width
        header.biHeight = -height  # 负数 = top-down，省掉一次垂直翻转
        header.biPlanes = 1
        header.biBitCount = 32
        header.biCompression = 0  # BI_RGB
        buffer = ctypes.create_string_buffer(width * height * 4)
        if not gdi32.GetDIBits(mdc, bmp, 0, height, buffer, ctypes.byref(header), 0):
            return None

        # GDI 给的是 BGRA，PIL 的 "BGRX" raw 模式正好跳过 alpha 直接读成 RGB
        return Image.frombuffer("RGB", (width, height), buffer, "raw", "BGRX", 0, 1)
    finally:
        gdi32.SelectObject(mdc, old)
        gdi32.DeleteObject(bmp)
        gdi32.DeleteDC(mdc)
        user32.ReleaseDC(hwnd, hdc)


def _grab(hwnd: int, name: str) -> Path:
    user32 = ctypes.windll.user32
    # 提到最前并显示，避免被其他窗口挡住截到别人的画面
    user32.SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_SHOWWINDOW)
    user32.SetForegroundWindow(hwnd)
    time.sleep(0.9)

    img = _capture_printwindow(hwnd)
    if img is None:
        # 兜底：个别窗口 PrintWindow 会失败（或返回全黑），退回屏幕抓取。
        # 这条路依赖窗口在前台且未被遮挡，所以上面先把窗口提到最前。
        left, top, right, bottom = _rect(hwnd)
        img = ImageGrab.grab(bbox=(left, top, right, bottom), all_screens=True)
        print("  提示：PrintWindow 失败，已退回 ImageGrab")

    out = SHOTS / name
    out.parent.mkdir(parents=True, exist_ok=True)
    img.save(out)
    print(f"  截图 {out.name}  {img.width}x{img.height}")
    return out


# ---------------------------------------------------------------- UIA
def _by_id(win: Any, automation_id: str) -> Any | None:
    try:
        for c in win.descendants():
            try:
                if c.automation_id() == automation_id:
                    return c
            except Exception:
                continue
    except Exception:
        pass
    return None


def _text_of(wrapper: Any | None) -> str:
    if wrapper is None:
        return ""
    for getter in ("window_text", "get_value"):
        try:
            value = getattr(wrapper, getter)()
            if value:
                return str(value).strip()
        except Exception:
            continue
    try:
        return str(wrapper.name).strip()
    except Exception:
        return ""


def _is_on(toggle: Any) -> bool | None:
    try:
        return bool(toggle.is_selected())
    except Exception:
        pass
    try:
        return bool(toggle.get_toggle_state())
    except Exception:
        return None


def _flip(toggle: Any) -> None:
    for action in ("toggle", "click_input"):
        try:
            getattr(toggle, action)()
            return
        except Exception:
            continue
    raise RuntimeError("既不能 toggle 也不能 click_input")


def _page_scroller(win: Any) -> Any | None:
    """页面主纵向滚动区。

    WinUI 3 的 ScrollViewer 是暴露 ScrollPattern 的（class_name 为 ScrollViewer、
    CurrentVerticallyScrollable 为 True），直接驱动它比注入滚轮可靠得多：
    滚轮要求窗口在前台、鼠标真的落在窗口上，而且会被内层横向滚动区吃掉。
    注意这里要按 class_name 过滤 —— 逐小时那条横向滚动条也是 ScrollViewer。
    """
    try:
        for c in win.descendants():
            try:
                if (c.element_info.class_name == "ScrollViewer"
                        and c.iface_scroll.CurrentVerticallyScrollable):
                    return c
            except Exception:
                continue
    except Exception:
        pass
    return None


def _scroll_until_visible(win: Any, automation_id: str, max_steps: int = 24) -> bool:
    """小步下滑，直到目标控件完整落在滚动区可视范围内。"""
    target = _page_scroller(win)
    if target is None:
        print("  ! 没找到页面滚动区")
        return False

    NO_AMOUNT, SMALL_INCREMENT = 2, 4
    viewport = target.rectangle()

    for _ in range(max_steps):
        node = _by_id(win, automation_id)
        if node is not None:
            rect = node.rectangle()
            if rect.height() > 0 and rect.top >= viewport.top - 4 and rect.bottom <= viewport.bottom + 4:
                return True

        before = target.iface_scroll.CurrentVerticalScrollPercent
        try:
            target.iface_scroll.Scroll(NO_AMOUNT, SMALL_INCREMENT)
        except Exception as exc:
            print(f"  ! 滚动失败: {type(exc).__name__}: {exc}")
            return False
        time.sleep(0.28)

        if target.iface_scroll.CurrentVerticalScrollPercent == before:
            return False

    return False


def _scroll_to_top(win: Any) -> None:
    target = _page_scroller(win)
    if target is None:
        return
    try:
        target.iface_scroll.SetScrollPercent(-1, 0)  # -1 = 横向不动
        time.sleep(0.5)
    except Exception:
        pass


# ---------------------------------------------------------------- 主流程
def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--app-path")
    parser.add_argument("--timeout", type=float, default=25.0)
    parser.add_argument("--keep-running", action="store_true")
    args = parser.parse_args()

    exe = _find_exe(args.app_path)
    env = dict(os.environ)
    env["SKYVIEW_DEMO"] = "1"

    print(f"启动 {exe}")
    proc = subprocess.Popen([str(exe)], env=env)
    hwnd = 0
    try:
        from pywinauto import Application

        # 先等主窗口真的出现，再连 UIA。直接连会撞上「进程已在、窗口还没 Create」的时序窗口。
        hwnd = _wait_main_window(proc.pid, args.timeout)
        print(f"窗口就绪 hwnd=0x{hwnd:X}  尺寸={_rect(hwnd)}")
        time.sleep(1.5)

        app = Application(backend="uia").connect(process=proc.pid, timeout=args.timeout)
        spec = app.window(title="SkyView")
        spec.wait("exists ready", timeout=args.timeout)
        win = spec.wrapper_object()
        hwnd = getattr(win, "handle", None) or hwnd
        time.sleep(1.5)  # 等首屏数据渲染（演示数据源同步返回）

        # ---- 1. 天气页 ----
        temp_before = _text_of(_by_id(win, "TemperatureText"))
        range_before = _text_of(_by_id(win, "TrendRangeText"))
        print(f"\n[天气页] 温度={temp_before}  趋势区间={range_before}")
        _grab(hwnd, "01-天气页.png")

        # ---- 1b/1c. 滚到温度趋势图、再到预报/生活指数 ----
        if _scroll_until_visible(win, "TrendTitle"):
            time.sleep(0.6)
            _grab(hwnd, "01b-温度趋势.png")
        if _scroll_until_visible(win, "ForecastTitle"):
            time.sleep(0.6)
            _grab(hwnd, "01c-预报与生活指数.png")
        _scroll_to_top(win)

        # ---- 2. 数据源页 ----
        nav = _by_id(win, "DataSourcesNavItem")
        if nav is None:
            raise SystemExit("找不到数据源导航项")
        nav.select()
        time.sleep(1.6)
        _grab(hwnd, "12-数据源页.png")

        # ---- 2b. 设置页（实时保存，无保存按钮） ----
        nav = _by_id(win, "SettingsNavItem")
        if nav is None:
            raise SystemExit("找不到设置导航项")
        nav.select()
        time.sleep(1.6)
        _grab(hwnd, "02-设置页.png")

        toggle = _by_id(win, "UnitToggle")
        before_state = _is_on(toggle)
        print(f"[设置页] UnitToggle 当前状态={before_state}")

        _flip(toggle)
        time.sleep(0.8)   # 实时保存：等防抖落盘 + 状态文本出现
        after_state = _is_on(toggle)
        print(f"[设置页] 翻转后状态={after_state}")
        print(f"[设置页] 自动保存状态文本={_text_of(_by_id(win, 'StatusText'))!r}")
        _grab(hwnd, "03-设置保存后.png")

        # 保存按钮应已移除（实时保存）
        if _by_id(win, "SaveSettingsButton") is not None:
            print("  ! 仍找到「保存设置」按钮，实时保存改造不完整")
            return 1

        # ---- 3. 回天气页看单位是否生效（不需要点任何保存） ----
        home = _by_id(win, "HomeNavItem")
        if home is not None:
            home.select()
            time.sleep(1.6)
        temp_after = _text_of(_by_id(win, "TemperatureText"))
        range_after = _text_of(_by_id(win, "TrendRangeText"))
        print(f"\n[回到天气页] 温度={temp_after}  趋势区间={range_after}")
        _grab(hwnd, "04-切换单位后.png")

        changed = temp_before != temp_after
        print(f"\n单位切换实时生效：{'是' if changed else '否'}"
              f"（{temp_before} → {temp_after}）")
        if not changed:
            print("  ! 温度文本没变化，检查 UnitSettings / TemperatureConverter 链路")
        return 0 if changed else 1
    finally:
        if hwnd:
            ctypes.windll.user32.SetWindowPos(
                hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE)
        if not args.keep_running:
            proc.terminate()
            try:
                proc.wait(timeout=8)
            except subprocess.TimeoutExpired:
                proc.kill()
            print("已关闭应用")


if __name__ == "__main__":
    sys.exit(main())
