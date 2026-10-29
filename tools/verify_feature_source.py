"""按功能分配数据源的应用内真实验证（自包含：自动改 preferences.json，跑完还原）。

验证点（指派：当前天气=OpenMeteo，天气预报=Wttr，逐小时/生活指数=自动）：
1. 主页「来源」显示 OpenMeteo（当前天气走了指派源）
2. 趋势卡「来源：Wttr」（天气预报走了指派源 wttr.in——与当前天气不同源）
3. 逐小时卡「来源：OpenMeteo」（未指派 → 自动链上第一个可用源）
4. 进程无崩溃、无 crash.log，截图存档

注意：默认窗口高 720px 时，趋势卡折在 ScrollViewer 可视区外，WinUI 的 UIA 树
不含视口外元素（pywinauto 直接读不到 ForecastSourceText）。所以起窗后先把窗口
拉高到 1000px，让趋势卡完整进入视口再读。

用法：python tools/verify_feature_source.py
"""

from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
PUBLISH = REPO_ROOT / "src/SkyView/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/publish"
SHOTS = REPO_ROOT / "docs" / "shots"
PREFS = Path(os.environ.get("LOCALAPPDATA", "")) / "SkyView" / "preferences.json"
PREFS_BAK = PREFS.with_suffix(".json.verifybak")
PW_RENDERFULLCONTENT = 0x00000002

# 本次验证的分配方案
ASSIGN = {"Current": "OpenMeteo", "Forecast": "Wttr"}


def _enable_dpi_awareness() -> None:
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


class _BH(ctypes.Structure):
    _fields_ = [
        ("biSize", wt.DWORD), ("biWidth", ctypes.c_long), ("biHeight", ctypes.c_long),
        ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
        ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", ctypes.c_long), ("biYPelsPerMeter", ctypes.c_long),
        ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD),
    ]


def _prep() -> None:
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
    GDI32.GetDIBits.argtypes = [wt.HDC, wt.HBITMAP, wt.UINT, wt.UINT, ctypes.c_void_p, ctypes.c_void_p, wt.UINT]


_prep()


def _rect(hwnd: int) -> tuple[int, int, int, int]:
    r = wt.RECT()
    USER32.GetWindowRect(hwnd, ctypes.byref(r))
    return r.left, r.top, r.right, r.bottom


def _find_main(pid: int) -> int:
    best: list[tuple[int, int]] = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def cb(h, _):
        o = wt.DWORD()
        USER32.GetWindowThreadProcessId(h, ctypes.byref(o))
        if o.value != pid or not USER32.IsWindowVisible(h):
            return True
        l, t, r, b = _rect(h)
        best.append(((r - l) * (b - t), h))
        return True

    USER32.EnumWindows(cb, 0)
    return max(best)[1] if best else 0


def _wait(pid: int, timeout: float = 30.0) -> int:
    dl = time.time() + timeout
    while time.time() < dl:
        h = _find_main(pid)
        if h:
            l, t, r, b = _rect(h)
            if (r - l) >= 400 and (b - t) >= 300:
                return h
        time.sleep(0.3)
    return 0


def _capture(hwnd: int, name: str):
    l, t, r, b = _rect(hwnd)
    w, h = r - l, b - t
    if w <= 0 or h <= 0:
        return None
    hdc = USER32.GetWindowDC(hwnd)
    mdc = GDI32.CreateCompatibleDC(hdc)
    bmp = GDI32.CreateCompatibleBitmap(hdc, w, h)
    old = GDI32.SelectObject(mdc, bmp)
    try:
        if not USER32.PrintWindow(hwnd, mdc, PW_RENDERFULLCONTENT):
            return None
        hdr = _BH()
        hdr.biSize = ctypes.sizeof(_BH)
        hdr.biWidth = w
        hdr.biHeight = -h
        hdr.biPlanes = 1
        hdr.biBitCount = 32
        buf = ctypes.create_string_buffer(w * h * 4)
        if not GDI32.GetDIBits(mdc, bmp, 0, h, buf, ctypes.byref(hdr), 0):
            return None
        from PIL import Image
        img = Image.frombuffer("RGB", (w, h), buf, "raw", "BGRX", 0, 1)
        out = SHOTS / name
        out.parent.mkdir(parents=True, exist_ok=True)
        img.save(out)
        return out
    finally:
        GDI32.SelectObject(mdc, old)
        GDI32.DeleteObject(bmp)
        GDI32.DeleteDC(mdc)
        USER32.ReleaseDC(hwnd, hdc)


def _write_assignments() -> None:
    """把分配合并进现有 preferences.json（保留单位等其它偏好），原文件备份。"""
    shutil.copy2(PREFS, PREFS_BAK)
    cfg = json.loads(PREFS.read_text(encoding="utf-8"))
    cfg["FeatureSources"] = dict(ASSIGN)
    PREFS.write_text(json.dumps(cfg, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"已写入功能分配 {ASSIGN}（备份 -> {PREFS_BAK.name}）")


def _restore_prefs() -> None:
    if PREFS_BAK.is_file():
        shutil.move(str(PREFS_BAK), str(PREFS))
        print("已还原 preferences.json")


def main() -> int:
    exe = PUBLISH / "SkyView.exe"
    if not exe.is_file():
        sys.exit(f"找不到 {exe}，先跑 dotnet publish")
    if not PREFS.is_file():
        sys.exit(f"找不到 {PREFS}")

    _write_assignments()

    crash_path = os.path.join(os.environ.get("LOCALAPPDATA", ""), "SkyView", "crash.log")
    if os.path.exists(crash_path):
        os.remove(crash_path)

    env = dict(os.environ)
    env.pop("SKYVIEW_DEMO", None)

    proc = subprocess.Popen([str(exe)], env=env)
    hwnd = _wait(proc.pid)
    if not hwnd:
        proc.terminate()
        _restore_prefs()
        sys.exit("30s 内没等到主窗口")

    # 窗口默认只有 720px 高，趋势卡会折在 ScrollViewer 可视区外，
    # 而 WinUI 的 UIA 树不含视口外元素——先拉高窗口让趋势卡完整可见再读。
    l, t, r, b = _rect(hwnd)
    USER32.MoveWindow(hwnd, l, t, 1000, 1000, True)

    USER32.SetForegroundWindow(hwnd)
    time.sleep(6.0)  # 启动后自动刷新一次（含新分配）

    results: list[tuple[str, bool, str]] = []
    try:
        from pywinauto import Application
        app = Application(backend="uia").connect(process=proc.pid)
        win = app.window(title="SkyView")

        # 再点一次刷新，确保用的是新分配（避开启动时可能命中的旧缓存快照）
        try:
            win.child_window(auto_id="RefreshButton").invoke()
        except Exception:
            pass
        time.sleep(10.0)

        def read_text(auto_id: str) -> str:
            try:
                return win.child_window(auto_id=auto_id).window_text()
            except Exception as ex:
                return f"读取失败：{ex}"

        current_text = read_text("SourceText")
        forecast_text = read_text("ForecastSourceText")
        hourly_text = read_text("HourlySourceText")

        results.append(("当前天气来源 == OpenMeteo（指派生效）", "OpenMeteo" in current_text, current_text))
        results.append(("预报来源 == Wttr（指派生效，与当前不同源）", "Wttr" in forecast_text, forecast_text))
        results.append(("逐小时来源 == OpenMeteo（未指派走自动链）", "OpenMeteo" in hourly_text, hourly_text))

        shot = _capture(hwnd, "10-功能数据源.png")
        results.append(("主页截图", shot is not None, str(shot.name) if shot else "失败"))

        alive = proc.poll() is None
        results.append(("进程存活（无崩溃）", alive, f"alive={alive}"))

        crash = ""
        if os.path.exists(crash_path):
            crash = open(crash_path, encoding="utf-8", errors="replace").read()[-400:]
        results.append(("无崩溃日志", not crash, crash[:200] if crash else "无"))

        print()
        width = max(len(n) for n, _, _ in results)
        passed = 0
        for n, ok_flag, detail in results:
            print(f"  {'✓' if ok_flag else '✗'} {n.ljust(width)}  {detail}")
            passed += 1 if ok_flag else 0
        print(f"\n共 {len(results)} 项：通过 {passed}，失败 {len(results) - passed}")
        return 0 if passed == len(results) else 1
    finally:
        if proc.poll() is None:
            proc.terminate()
            try:
                proc.wait(timeout=8)
            except subprocess.TimeoutExpired:
                proc.kill()
        _restore_prefs()
        print("已关闭应用")


if __name__ == "__main__":
    sys.exit(main())
