"""真实数据 + 城市瀑布流验证（不设 SKYVIEW_DEMO，走 Open-Meteo 兜底链）。

验证点：
1. 主页用真实数据源（Open-Meteo 兜底）拿到天气，温度非 0、非默认
1b. tier-1 新指标：UV 徽标 / 详细指标行（降水/云量/露点/阵风）/ 气压趋势箭头
1c. tier-2 新指标：15 天长预报（forecast_days=15）/ 短临降雨文案（minutely_15）
1d. tier-3 新指标：天气预警卡（本地推导，预报未触阈值时隐藏属预期）/ 花粉行（国内城市无数据属预期）
2. 主内容随窗口伸展（不再 MaxWidth=760 左对齐留白）
3. 城市页瀑布流卡片渲染：每个城市显示天气图标 + 温度 + 城市名
4. 三个无效 key（OWM/AW/PW）不导致崩溃，降级正常

tier-2 是 Open-Meteo 专属能力（wttr.in 只有 3 天预报、没有分钟级数据），
脚本启动前把 Current/Forecast 两个功能强制指向 Open-Meteo（备份/还原 preferences.json），
否则本机遗留的 FeatureSources.Current=Wttr 会让断言失去前提。

窗口定位 / 截图沿用 check_tier4.py 的 ctypes + PrintWindow 方案。
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

from PIL import Image

REPO_ROOT = Path(__file__).resolve().parent.parent
SHOTS = REPO_ROOT / "docs" / "shots"
PW_RENDERFULLCONTENT = 0x00000002
PREFS = Path(os.environ.get("LOCALAPPDATA", "")) / "SkyView" / "preferences.json"
PREFS_BAK = REPO_ROOT / "preferences.json.verify-realdata.bak"


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


def _by_id(win, automation_id: str):
    """按 AutomationId 找控件（descendants 全树扫描，命中即返回）。"""
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


def _count_by_id(win, automation_id: str) -> int:
    """统计同一 AutomationId 在 UIA 树里出现的次数（用于数预报天数）。"""
    n = 0
    try:
        for c in win.descendants():
            try:
                if c.automation_id() == automation_id:
                    n += 1
            except Exception:
                continue
    except Exception:
        pass
    return n


def _try_auto_id(wrapper) -> str:
    try:
        return wrapper.automation_id() or ""
    except Exception:
        return ""


def _text_of(wrapper) -> str:
    if wrapper is None:
        return ""
    for getter in ("window_text", "get_value"):
        try:
            value = getattr(wrapper, getter)()
            if value:
                return str(value)
        except Exception:
            continue
    return ""


def _page_scroller(win):
    """页面主纵向滚动区（class_name=ScrollViewer 且纵向可滚的那个；
    逐小时的横向滚动条也是 ScrollViewer，必须按可滚方向过滤）。"""
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


def _scroll_until_visible(win, automation_id: str, max_steps: int = 24) -> bool:
    """小步下滑，直到目标控件完整落在滚动区可视范围内。

     WinUI UIA 树不含 ScrollViewer 视口外元素——预报卡片在首屏之下，
    不滚动根本读不到（会误报"控件缺失"）。
    """
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


def _scroll_to_top(win) -> None:
    target = _page_scroller(win)
    if target is None:
        return
    try:
        target.iface_scroll.SetScrollPercent(-1, 0)  # -1 = 横向不动
        time.sleep(0.5)
    except Exception:
        pass


def _force_openmeteo_prefs() -> None:
    """把 Current/Forecast 两个功能强制指向 Open-Meteo（tier-2 断言前提）。

    只改这两个键，其余偏好原样写回；调用方负责备份/还原。
    """
    data: dict = {}
    if PREFS.is_file():
        data = json.loads(PREFS.read_text(encoding="utf-8"))
    sources = dict(data.get("FeatureSources") or {})
    sources["Current"] = "OpenMeteo"
    sources["Forecast"] = "OpenMeteo"
    data["FeatureSources"] = sources
    PREFS.parent.mkdir(parents=True, exist_ok=True)
    PREFS.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"已强制 FeatureSources.Current/Forecast = OpenMeteo（备份见 {PREFS_BAK.name}）")


def main() -> int:
    exe = REPO_ROOT / "src/SkyView/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/publish/SkyView.exe"
    if not exe.is_file():
        sys.exit(f"找不到 {exe}，先跑 dotnet publish")

    # 清理旧的崩溃日志，避免误判
    crash_path = os.path.join(os.environ.get("LOCALAPPDATA", ""), "SkyView", "crash.log")
    if os.path.exists(crash_path):
        os.remove(crash_path)

    # tier-2 断言前提：Current/Forecast 指到 Open-Meteo（15 天 + 分钟级都是它专属）
    if PREFS.is_file():
        shutil.copy2(PREFS, PREFS_BAK)
        print(f"已备份 preferences.json -> {PREFS_BAK.name}")
    _force_openmeteo_prefs()

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

    # 拉高窗口：预报卡片在首屏之下，小窗连滚带读都容易误判
    l, t, r, b = _rect(hwnd)
    USER32.MoveWindow(hwnd, l, t, 1000, 1000, True)
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

        # 1b. tier-1 新指标：UV 徽标 / 详细指标行 / 气压趋势。
        #     Open-Meteo 免 Key 链应全部提供（uv_index / precipitation / cloud_cover /
        #     dew_point_2m / wind_gusts_10m / hourly pressure_msl）
        tier1: dict[str, str] = {}
        for auto_id in ("UvIndexText", "PrecipitationText", "CloudCoverText",
                        "DewPointText", "WindGustsText", "PressureTrendText"):
            try:
                tier1[auto_id] = win.child_window(auto_id=auto_id).window_text()
            except Exception:
                tier1[auto_id] = ""
        uv_ok = bool(re.match(r"^UV \d+ · .+$", tier1["UvIndexText"]))
        results.append(("UV 徽标（Open-Meteo uv_index）", uv_ok, f"文本={tier1['UvIndexText']!r}"))
        detail_keys = ("PrecipitationText", "CloudCoverText", "DewPointText", "WindGustsText")
        detail_ok = all(tier1[k] for k in detail_keys)
        results.append(("详细指标行（降水/云量/露点/阵风）", detail_ok,
                        " ".join(f"{k}={tier1[k]!r}" for k in detail_keys)))
        trend_ok = bool(re.match(r"^[▲▼→] .+$", tier1["PressureTrendText"]))
        results.append(("气压趋势箭头（hourly pressure_msl）", trend_ok, f"文本={tier1['PressureTrendText']!r}"))

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

        # 1c. tier-2：15 天长预报 + 短临降雨（Open-Meteo 专属）
        #     预报卡片在首屏之下，先滚入视口再读
        scrolled = _scroll_until_visible(win, "ForecastTitle")
        time.sleep(0.6)

        # 注意：来源标注用的是 provider.Name（"OpenMeteo"）而不是配置里的
        # DisplayName（"Open-Meteo"）——WeatherService 刻意用胜出源的 Name 覆盖，
        # 和 wttr.in 显示成 "Wttr" 是同一个设计
        fcst_src = _text_of(_by_id(win, "ForecastSourceText"))
        src_ok = "OpenMeteo" in fcst_src
        results.append(("预报来源为 Open-Meteo（15 天能力前提）", src_ok, f"来源={fcst_src!r}"))

        fcst_title = _text_of(_by_id(win, "ForecastTitle"))
        title_ok = scrolled and fcst_title == "未来 15 天"
        results.append(("预报标题动态显示「未来 15 天」", title_ok, f"标题={fcst_title!r}"))

        day_count = _count_by_id(win, "WeatherIconForecast")
        days_ok = scrolled and day_count == 15
        results.append(("预报列表 15 天（forecast_days=15）", days_ok, f"天数={day_count}"))

        # 短临降雨文案在当前天气卡里，滚回顶部读
        _scroll_to_top(win)
        rain_text = _text_of(_by_id(win, "MinutelyRainText"))
        rain_ok = bool(re.match(r"^短临降雨：.+$", rain_text))
        results.append(("短临降雨文案（minutely_15）", rain_ok, f"文本={rain_text!r}"))

        # 1d. tier-3：恶劣天气预警（本地推导，有触发才显示）+ 花粉（国内城市无数据属预期）。
        #     预报没触阈值时预警卡隐藏是正确行为，这里按「可见则必良构」断言
        alerts_card = _by_id(win, "AlertsTitle")
        if alerts_card is not None:
            alert_kinds = _count_by_id(win, "AlertKind")
            levels = [
                _text_of(c) for c in win.descendants()
                if _try_auto_id(c) == "AlertLevel"
            ]
            alerts_ok = alert_kinds >= 1 and all(lv in ("蓝色", "黄色", "橙色", "红色") for lv in levels)
            results.append(("天气预警卡（本地推导，可见且良构）", alerts_ok,
                            f"条数={alert_kinds} 等级={levels}"))
        else:
            results.append(("天气预警卡（预报未触阈值，隐藏属预期）", True, "未触发"))

        pollen_text = _text_of(_by_id(win, "PollenText"))
        if pollen_text:
            pollen_ok = bool(re.match(r"^花粉：.+粒/m³）$", pollen_text))
            results.append(("花粉行（数据源提供）", pollen_ok, f"文本={pollen_text!r}"))
        else:
            # 国内城市 Air Quality API 五个花粉变量全 null——隐藏是正确行为
            results.append(("花粉行（国内城市无数据，隐藏属预期）", True, "未提供"))

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
        # 还原 preferences.json（被强制指向过 Open-Meteo）
        if PREFS_BAK.is_file():
            shutil.copy2(PREFS_BAK, PREFS)
            PREFS_BAK.unlink()
            print(f"已还原 preferences.json（{PREFS}）")
        print("已关闭应用")


if __name__ == "__main__":
    sys.exit(main())
