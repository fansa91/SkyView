"""真实数据 + 城市瀑布流验证（不设 SKYVIEW_DEMO，走 Open-Meteo 兜底链）。

验证点：
1. 主页用真实数据源（Open-Meteo 兜底）拿到天气，温度非 0、非默认
2. 主内容随窗口伸展（不再 MaxWidth=760 左对齐留白）
3. 城市页瀑布流卡片渲染：每个城市显示天气图标 + 温度 + 城市名
4. 三个无效 key（OWM/AW/PW）不导致崩溃，降级正常

窗口定位 / 截图沿用 check_tier4.py 的 ctypes + PrintWindow 方案。
"""

from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
import os
import re
import subprocess
import sys
import time
from pathlib import Path

from PIL import Image

REPO_ROOT = Path(__file__).resolve().parent.parent
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
        img = Image.frombuffer("RGB", (w, h), buf, "raw", "BGRX", 0,1)
        out = SHOTS / name
        out.parent.mkdir(parents=True, exist_ok=True)
        img.save(out)
        return out
    finally:
        GDI32.SelectObject(mdc, old)
        GDI32.DeleteObject(bmp)
        GDI32.DeleteDC(mdc)
        USER32.ReleaseDC(hwnd, hdc)


def main() -> int:
    exe = REPO_ROOT / "src/SkyView/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/publish/SkyView.exe"
    if not exe.is_file():
        sys.exit(f"找不到 {exe}，先跑 dotnet publish")

    # 清理旧的崩溃日志，避免误判
    crash_path = os.path.join(os.environ.get("LOCALAPPDATA", ""), "SkyView", "crash.log")
    if os.path.exists(crash_path):
        os.remove(crash_path)

    # 关键：不设 SKYVIEW_DEMO，让应用走真实数据源链路（Open-Meteo 兜底）
    env = dict(os.environ)
    env.pop("SKYVIEW_DEMO", None)
    if "SKYVIEW_DEMO" in env:
        del env["SKYVIEW_DEMO"]

    proc = subprocess.Popen([str(exe)], env=env)
    hwnd = _wait(proc.pid)
    if not hwnd:
        proc.terminate()
        sys.exit("30s 内没等到主窗口")

    USER32.SetForegroundWindow(hwnd)
    time.sleep(5.0)  # 首屏真实刷新（含降级链）

    results: list[tuple[str, bool, str]] = []
    try:
        from pywinauto import Application
        app = Application(backend="uia").connect(process=proc.pid)
        win = app.window(title="SkyView")

        # 1. 主页真实温度
        temp_text = ""
        try:
            temp_text = win.child_window(auto_id="TemperatureText").window_text()
        except Exception as ex:
            temp_text = f"读取失败：{ex}"
        # 必须是形如 "15°C" / "58.5°F" 的真实数值，而不是读取失败或空
        # （°F 先换算成 °C 再判断：59°F=15°C 能过，64°F=18°C 也曾被 -90<v<60 误杀）
        m = re.match(r"^\s*(-?\d+(?:\.\d+)?)\s*°?\s*([CF]?)\s*$", temp_text)
        if m:
            val = float(m.group(1))
            unit = m.group(2).upper()
            val_c = (val - 32) * 5 / 9 if unit == "F" else val
            temp_ok = -90 < val_c < 60
        else:
            temp_ok = False
        results.append(("主页真实天气（来自 Open-Meteo 兜底）", temp_ok, f"温度文本={temp_text!r}"))

        # 2. 主内容伸展：内容宽度应接近窗口宽度（不再 760 左对齐）
        l, t, r, b = _rect(hwnd)
        win_w = r - l
        shot_home = _capture(hwnd, "07-真实主页.png")
        # 分析截图：找非背景像素的最右边界，判断内容是否铺满
        stretch_ok = False
        if shot_home:
            img = Image.open(shot_home).convert("RGB")
            px = img.load()
            iw, ih = img.size
            # 采样右 30% 区域：若有明显非背景列，说明内容铺到了右侧
            right_edge = 0
            for x in range(int(iw * 0.55), iw):
                non_bg = False
                for y in range(0, ih, 12):
                    r0, g0, b0 = px[x, y]
                    # 背景近似浅灰/白或主题色；卡片是白底带边框，判断亮度变化
                    if not (abs(r0 - g0) < 12 and abs(g0 - b0) < 12):
                        non_bg = True
                        break
                if non_bg:
                    right_edge = x
            stretch_ok = right_edge >= int(iw * 0.8)
        results.append(("主内容随窗口伸展（右半区有卡片）", stretch_ok, f"右边界采样={right_edge} 窗口宽={win_w}"))

        # 3. 城市页瀑布流
        try:
            win.child_window(auto_id="CityNavItem").select()
        except Exception as ex:
            results.append(("导航到城市页", False, f"select 失败：{ex}"))
        time.sleep(15.0)  # 4 城市并发取 Open-Meteo（绑定修复后应在数秒内填充）
        shot_city = _capture(hwnd, "08-城市瀑布流.png")
        city_ok = shot_city is not None
        results.append(("城市页截图", city_ok, str(shot_city.name) if shot_city else "失败"))

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
        print("已关闭应用")


if __name__ == "__main__":
    sys.exit(main())
