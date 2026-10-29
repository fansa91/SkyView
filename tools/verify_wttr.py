"""wttr.in 数据源的应用内真实验证（自包含：自动改发布目录的 appsettings，跑完还原）。

验证点：
1. 把 wttr 置顶优先级（并临时关掉其它源），强制应用走 wttr.in 链路
2. 主页显示真实温度（正则匹配，排除"读取失败"等假阳性）
3. 主页"来源"标签 == "wttr.in" —— 证明数据确实由 wttr.in 返回
4. 截图存档；进程无崩溃、无 crash.log

用法：python tools/verify_wttr.py
"""

from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
import json
import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
PUBLISH = REPO_ROOT / "src/SkyView/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/publish"
SETTINGS = PUBLISH / "appsettings.json"
BACKUP = PUBLISH / "appsettings.json.verifybak"
SHOTS = REPO_ROOT / "docs" / "shots"
PW_RENDERFULLCONTENT = 0x00000002


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


def _force_wttr_top() -> None:
    """把发布目录的 appsettings 改成只留 Wttr(0)/OpenMeteo(5)，Wttr 置顶。"""
    shutil.copy2(SETTINGS, BACKUP)
    cfg = json.loads(SETTINGS.read_text(encoding="utf-8"))
    for p in cfg.get("WeatherProviders", []):
        name = p.get("Name")
        if name == "Wttr":
            p["Priority"] = 0
            p["Enabled"] = True
        elif name in ("QWeather", "Seniverse"):
            p["Enabled"] = False
    SETTINGS.write_text(json.dumps(cfg, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"已将 wttr.in 置顶（备份 -> {BACKUP.name}）")


def _restore_settings() -> None:
    if BACKUP.is_file():
        shutil.move(str(BACKUP), str(SETTINGS))
        print("已还原 appsettings.json")


def main() -> int:
    exe = PUBLISH / "SkyView.exe"
    if not exe.is_file():
        sys.exit(f"找不到 {exe}，先跑 dotnet publish")
    if not SETTINGS.is_file():
        sys.exit(f"找不到 {SETTINGS}")

    _force_wttr_top()

    crash_path = os.path.join(os.environ.get("LOCALAPPDATA", ""), "SkyView", "crash.log")
    if os.path.exists(crash_path):
        os.remove(crash_path)

    env = dict(os.environ)
    env.pop("SKYVIEW_DEMO", None)

    proc = subprocess.Popen([str(exe)], env=env)
    hwnd = _wait(proc.pid)
    if not hwnd:
        proc.terminate()
        _restore_settings()
        sys.exit("30s 内没等到主窗口")

    USER32.SetForegroundWindow(hwnd)
    time.sleep(6.0)  # 首屏真实刷新（wttr.in 偶有首包慢，多等 1s）

    results: list[tuple[str, bool, str]] = []
    try:
        from pywinauto import Application
        app = Application(backend="uia").connect(process=proc.pid)
        win = app.window(title="SkyView")

        # 1. 真实温度（排除"读取失败"假阳性；°F 要先换算成 °C 再按气温常识范围判断）
        temp_text = ""
        try:
            temp_text = win.child_window(auto_id="TemperatureText").window_text()
        except Exception as ex:
            temp_text = f"读取失败：{ex}"
        m = re.match(r"^\s*(-?\d+(?:\.\d+)?)\s*°?\s*([CF]?)\s*$", temp_text)
        if m:
            val = float(m.group(1))
            unit = m.group(2).upper()
            val_c = (val - 32) * 5 / 9 if unit == "F" else val
            temp_ok = -90 < val_c < 60
        else:
            temp_ok = False
        results.append(("主页真实温度", temp_ok, f"温度文本={temp_text!r}"))

        # 2. 来源必须走 wttr.in（WeatherService 会把 Source 盖成 provider.Name，即 "Wttr"）
        source_text = ""
        try:
            source_text = win.child_window(auto_id="SourceText").window_text()
        except Exception as ex:
            source_text = f"读取失败：{ex}"
        source_ok = "wttr" in source_text.lower()
        results.append(("数据来源 == wttr.in", source_ok, f"来源文本={source_text!r}"))

        # 3. 截图
        shot = _capture(hwnd, "09-wttr-主页.png")
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
        _restore_settings()
        print("已关闭应用")


if __name__ == "__main__":
    sys.exit(main())
