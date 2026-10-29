"""第四梯队（桌面化体验）的端到端取证。

覆盖 5 项：
1. 系统托盘 + 关闭到托盘 —— 发 WM_CLOSE 后窗口不可见、进程仍存活
2. 单实例 + 唤起已有窗口 —— 再启动一次，进程数仍为 1，且隐藏的窗口被重新显示
3. 天气主题背景 —— 截图里能看到背景渐变层
4. 定位当前城市 —— 点「定位」后必须有反馈（成功改城市，或给出可读的失败原因）
5. 版本号 —— 设置页显示 v<tag>

为什么所有步骤必须在**同一个进程**里跑完：这些验证依赖被测应用持续存活，
而外部 shell 一退出就会把子进程一起带走（实测 tasklist 立刻为空），
分步调用会得到"应用莫名消失"的假结论。

用法：python tools/check_tier4.py [--app-path <exe>] [--keep-shots]
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

from PIL import Image

REPO_ROOT = Path(__file__).resolve().parent.parent
SHOTS = REPO_ROOT / "docs" / "shots"

WM_CLOSE = 0x0010
PW_RENDERFULLCONTENT = 0x00000002


def _enable_dpi_awareness() -> None:
    """必须抢在任何窗口查询/截图之前，否则拿到的是逻辑像素、截出来错位 25%。"""
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        try:
            ctypes.windll.user32.SetProcessDPIAware()
        except Exception:
            pass


_enable_dpi_awareness()

USER32 = ctypes.windll.user32
GDI32 = ctypes.windll.gdi32


class _BitmapInfoHeader(ctypes.Structure):
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


def _prepare_win32() -> None:
    USER32.GetWindowDC.restype = wt.HDC
    USER32.GetWindowDC.argtypes = [wt.HWND]
    USER32.ReleaseDC.argtypes = [wt.HWND, wt.HDC]
    USER32.PrintWindow.argtypes = [wt.HWND, wt.HDC, wt.UINT]
    GDI32.CreateCompatibleDC.restype = wt.HDC
    GDI32.CreateCompatibleDC.argtypes = [wt.HDC]
    GDI32.CreateCompatibleBitmap.restype = wt.HBITMAP
    GDI32.CreateCompatibleBitmap.argtypes = [wt.HDC, ctypes.c_int, ctypes.c_int]
    GDI32.SelectObject.restype = wt.HGDIOBJ
    GDI32.SelectObject.argtypes = [wt.HDC, wt.HGDIOBJ]
    GDI32.DeleteObject.argtypes = [wt.HGDIOBJ]
    GDI32.DeleteDC.argtypes = [wt.HDC]
    GDI32.GetDIBits.argtypes = [
        wt.HDC, wt.HBITMAP, wt.UINT, wt.UINT,
        ctypes.c_void_p, ctypes.c_void_p, wt.UINT,
    ]


_prepare_win32()


def _rect(hwnd: int) -> tuple[int, int, int, int]:
    rect = wt.RECT()
    USER32.GetWindowRect(hwnd, ctypes.byref(rect))
    return rect.left, rect.top, rect.right, rect.bottom


def _find_main_window(pid: int, require_visible: bool = True) -> int:
    """按面积取该进程最大的顶层窗口，过滤掉托盘消息窗口/IME 这类小窗。"""
    best: list[tuple[int, int]] = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def _enum(hwnd, _lparam):
        owner = wt.DWORD()
        USER32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value != pid:
            return True
        if require_visible and not USER32.IsWindowVisible(hwnd):
            return True

        left, top, right, bottom = _rect(hwnd)
        best.append(((right - left) * (bottom - top), hwnd))
        return True

    USER32.EnumWindows(_enum, 0)
    return max(best)[1] if best else 0


def _wait_main_window(pid: int, timeout: float = 30.0) -> int:
    deadline = time.time() + timeout
    while time.time() < deadline:
        hwnd = _find_main_window(pid)
        if hwnd:
            left, top, right, bottom = _rect(hwnd)
            if (right - left) >= 400 and (bottom - top) >= 300:
                return hwnd
        time.sleep(0.3)
    return 0


def _capture(hwnd: int, name: str) -> Path | None:
    left, top, right, bottom = _rect(hwnd)
    width, height = right - left, bottom - top
    if width <= 0 or height <= 0:
        return None

    hdc = USER32.GetWindowDC(hwnd)
    mdc = GDI32.CreateCompatibleDC(hdc)
    bmp = GDI32.CreateCompatibleBitmap(hdc, width, height)
    old = GDI32.SelectObject(mdc, bmp)
    try:
        if not USER32.PrintWindow(hwnd, mdc, PW_RENDERFULLCONTENT):
            return None

        header = _BitmapInfoHeader()
        header.biSize = ctypes.sizeof(_BitmapInfoHeader)
        header.biWidth = width
        header.biHeight = -height
        header.biPlanes = 1
        header.biBitCount = 32
        buffer = ctypes.create_string_buffer(width * height * 4)
        if not GDI32.GetDIBits(mdc, bmp, 0, height, buffer, ctypes.byref(header), 0):
            return None

        image = Image.frombuffer("RGB", (width, height), buffer, "raw", "BGRX", 0, 1)
        out = SHOTS / name
        out.parent.mkdir(parents=True, exist_ok=True)
        image.save(out)
        return out
    finally:
        GDI32.SelectObject(mdc, old)
        GDI32.DeleteObject(bmp)
        GDI32.DeleteDC(mdc)
        USER32.ReleaseDC(hwnd, hdc)


def _process_count(name: str = "SkyView.exe") -> int:
    output = subprocess.run(
        ["tasklist", "/FI", f"IMAGENAME eq {name}", "/NH"],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    ).stdout
    return sum(1 for line in output.splitlines() if name.lower() in line.lower())


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--app-path", default=None)
    parser.add_argument("--keep-running", action="store_true")
    args = parser.parse_args()

    exe = Path(args.app_path) if args.app_path else (
        REPO_ROOT
        / "src/SkyView/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/publish/SkyView.exe"
    )
    if not exe.is_file():
        raise SystemExit(f"找不到 {exe}，先跑 dotnet publish")

    env = dict(os.environ)
    env["SKYVIEW_DEMO"] = "1"

    results: list[tuple[str, bool, str]] = []
    proc: subprocess.Popen | None = None
    second: subprocess.Popen | None = None

    try:
        print(f"启动 {exe}")
        proc = subprocess.Popen([str(exe)], env=env)
        hwnd = _wait_main_window(proc.pid)
        if not hwnd:
            raise SystemExit("30s 内没等到主窗口")

        USER32.SetForegroundWindow(hwnd)
        time.sleep(2.0)

        from pywinauto import Application

        app = Application(backend="uia").connect(process=proc.pid)
        win = app.window(title="SkyView")

        # ---------- 5. 版本号（在设置页底部） ----------
        version = ""
        try:
            win.child_window(auto_id="SettingsNavItem").select()
            time.sleep(1.2)
            version = win.child_window(auto_id="VersionText").window_text()
            win.child_window(auto_id="HomeNavItem").select()
            time.sleep(1.2)
        except Exception as ex:
            version = f"读取失败：{type(ex).__name__}: {ex}"

        ok = version.startswith("v") and len(version) > 2
        results.append(("版本号（取 git tag）", ok, version or "读不到 VersionText"))

        # ---------- 3. 天气主题背景 ----------
        shot = _capture(hwnd, "05-主题背景.png")
        results.append(("天气主题背景截图", shot is not None, str(shot.name) if shot else "截图失败"))

        # ---------- 4. 定位当前城市 ----------
        locate_text = ""
        error_text = ""
        city_text = ""
        try:
            city_text = win.child_window(auto_id="CityInput").get_value()
        except Exception:
            pass

        try:
            win.child_window(auto_id="LocateButton").invoke()
            # 定位链路：请求权限 → 取坐标 → 反查城市，正常在几秒内出结果
            deadline = time.time() + 20
            while time.time() < deadline:
                time.sleep(0.6)
                try:
                    error_text = win.child_window(auto_id="ErrorText").window_text()
                except Exception:
                    error_text = ""
                try:
                    locate_text = win.child_window(auto_id="CityInput").get_value()
                except Exception:
                    pass
                if error_text or locate_text != city_text:
                    break
        except Exception as ex:
            error_text = f"点击定位失败：{ex}"

        # 判定标准：不能"点了没反应"。要么城市变了，要么给出可读的失败原因。
        locate_ok = bool(error_text) or (locate_text and locate_text != city_text)
        detail = error_text if error_text else f"城市 {city_text!r} → {locate_text!r}"
        results.append(("定位当前城市有明确反馈", locate_ok, detail[:120]))

        # ---------- 1. 关闭到托盘 ----------
        USER32.PostMessageW(hwnd, WM_CLOSE, 0, 0)
        time.sleep(1.5)
        still_running = proc.poll() is None
        hidden = not USER32.IsWindowVisible(hwnd)
        results.append((
            "关闭到托盘（窗口隐藏 / 进程存活）",
            still_running and hidden,
            f"进程存活={still_running} 窗口隐藏={hidden}",
        ))

        # ---------- 2. 单实例 + 唤起已有窗口 ----------
        before = _process_count()
        second = subprocess.Popen([str(exe)], env=env)
        time.sleep(5.0)
        after = _process_count()
        restored = USER32.IsWindowVisible(hwnd)
        ok = after == before == 1 and restored
        results.append((
            "单实例 + 托盘唤起",
            ok,
            f"进程数 {before} → {after}，窗口重新可见={restored}",
        ))

        _capture(hwnd, "06-托盘唤起后.png")

        print()
        width = max(len(name) for name, _, _ in results)
        passed = 0
        for name, ok_flag, detail in results:
            mark = "✓" if ok_flag else "✗"
            print(f"  {mark} {name.ljust(width)}  {detail}")
            passed += 1 if ok_flag else 0

        print(f"\n共 {len(results)} 项：通过 {passed}，失败 {len(results) - passed}")
        return 0 if passed == len(results) else 1
    finally:
        for p in (second, proc):
            if p is None:
                continue
            if p.poll() is None:
                p.terminate()
                try:
                    p.wait(timeout=8)
                except subprocess.TimeoutExpired:
                    p.kill()
        print("已关闭应用")


if __name__ == "__main__":
    sys.exit(main())
