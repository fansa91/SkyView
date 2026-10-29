"""SkyView 的 pywinauto 功能测试（WinUI 3 / UIA 后端）。

与 check_ui_layout.py（只做只读布局测量）互补：
本脚本会**真的操作界面** —— 改城市、点刷新、切导航页，验证交互链路是否通。

用法:
    python tools/check_ui_pywinauto.py --pid 12345
    python tools/check_ui_pywinauto.py --launch
    python tools/check_ui_pywinauto.py --pid 12345 --json > report.json
    python tools/check_ui_pywinauto.py --pid 12345 --skip-functional   # 只做布局

退出码: 0 = 无 Error；1 = 存在 Error 级问题。

关于 pywinauto 在 WinUI 3 上的几个坑（都已在本脚本里绕过）:
1. `Application.connect(process=pid).windows()[0]` 返回的是 **UIAWrapper**，没有 `child_window()`；
   要用 `app.window(title=...)` 拿到 **WindowSpecification** 才能按 auto_id 查找。
2. `app.window(process=pid)` 会报 KeywordError（connect 已绑定进程，不能再传 process）。
3. AutomationId 必须显式写在 XAML 里才有；ItemTemplate 内部的控件（如每行 CheckBox）通常没有，
   需要靠 `descendants()` 按控件类型统计。
4. NavigationViewItem 用 `select()` 切换页面，Button 用 `invoke()` 点击，TextBox 用 `set_edit_text()` 输入。
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
from dataclasses import dataclass, field
from typing import Any

DEFAULT_EXE_CANDIDATES = [
    r"src\SkyView\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\publish\SkyView.exe",
    r"src\SkyView\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\SkyView.exe",
]

WINDOW_TITLE = "SkyView"


@dataclass
class Report:
    app: str = "SkyView"
    tool: str = "pywinauto (uia backend)"
    process_id: int = 0
    window_title: str = ""
    class_name: str = ""
    rectangle: dict[str, int] = field(default_factory=dict)
    nav_items: list[str] = field(default_factory=list)
    has_city_input: bool = False
    has_refresh_button: bool = False
    functional_checked: bool = False
    city_input_value: str = ""
    refresh_invoked: bool = False
    loading_observed: bool = False
    error_text: str = ""
    temperature_text: str = ""
    has_empty_hint: bool = False
    # 数据区块（有数据时才应出现）
    has_current_card: bool = False
    has_air_quality: bool = False
    has_hourly_section: bool = False
    has_trend_chart: bool = False
    has_life_index: bool = False
    has_cache_hint: bool = False
    has_icon_current: bool = False
    has_icon_hourly: bool = False
    has_icon_forecast: bool = False
    icon_current_name: str = ""
    trend_range_text: str = ""
    # tier-2 / tier-3 区块（演示数据下必须出现）
    has_minutely_rain: bool = False
    has_pollen: bool = False
    has_alerts_card: bool = False
    alert_count: int = 0
    minutely_rain_text: str = ""
    pollen_text: str = ""
    settings_checked: bool = False
    data_sources_checked: bool = False
    has_api_key_box: bool = False
    has_priority_box: bool = False
    has_unit_toggle: bool = False
    has_auto_refresh_box: bool = False
    has_test_button: bool = False
    has_feature_sources_card: bool = False
    has_version_text: bool = False
    provider_count: int = 0
    issues: list[dict[str, str]] = field(default_factory=list)
    exit_code: int = 0

    def add(self, level: str, message: str) -> None:
        self.issues.append({"level": level, "message": message})
        if level == "Error":
            self.exit_code = 1


def _rect(wrapper: Any) -> dict[str, int]:
    try:
        r = wrapper.rectangle()
        return {
            "left": r.left,
            "top": r.top,
            "right": r.right,
            "bottom": r.bottom,
            "width": r.width(),
            "height": r.height(),
        }
    except Exception:
        return {}


def _by_id(wrapper: Any, automation_id: str) -> Any | None:
    """在全树里按 AutomationId 取第一个控件（比 child_window 更耐深层嵌套）。"""
    try:
        for c in wrapper.descendants():
            try:
                if c.automation_id() == automation_id:
                    return c
            except Exception:
                continue
    except Exception:
        pass
    return None


def _try_automation_id(wrapper: Any) -> str:
    """取 AutomationId，取不到返回空串（descendants 里混着非控件节点）。"""
    try:
        return wrapper.automation_id() or ""
    except Exception:
        return ""


def _all_by_id(wrapper: Any, automation_id: str) -> list[Any]:
    result = []
    try:
        for c in wrapper.descendants():
            try:
                if c.automation_id() == automation_id:
                    result.append(c)
            except Exception:
                continue
    except Exception:
        pass
    return result


def _text_of(wrapper: Any) -> str:
    """尽力取出控件显示的文本（Static 用 window_text，Edit 用 value）。"""
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


def _launch(exe_path: str) -> subprocess.Popen[bytes]:
    env = dict(os.environ)
    env.setdefault("DOTNET_ROOT", r"C:\dotnet")
    return subprocess.Popen([exe_path], env=env)


def _wait_window(app: Any, timeout: float) -> Any:
    spec = app.window(title=WINDOW_TITLE)
    spec.wait("exists ready", timeout=timeout)
    return spec


def run(args: argparse.Namespace) -> Report:
    report = Report()

    try:
        from pywinauto import Application
    except ImportError:
        report.add("Error", "未安装 pywinauto，请执行: pip install pywinauto")
        return report

    proc = None
    pid = args.pid
    if args.launch:
        exe = args.app_path
        if not exe:
            for cand in DEFAULT_EXE_CANDIDATES:
                if os.path.isfile(cand):
                    exe = cand
                    break
        if not exe or not os.path.isfile(exe):
            report.add("Error", "未找到 SkyView.exe，请用 --app-path 指定")
            return report
        proc = _launch(exe)
        pid = proc.pid
        print(f"[INFO] 已启动 {exe} (PID={pid})")

    if not pid:
        report.add("Error", "必须提供 --pid 或 --launch")
        return report

    report.process_id = pid

    try:
        app = Application(backend="uia").connect(process=pid, timeout=args.timeout)
        spec = _wait_window(app, args.timeout)
        win = spec.wrapper_object()
    except Exception as exc:
        report.add("Error", f"连接窗口失败: {type(exc).__name__}: {exc}")
        return report

    # ---------- 1. 窗口基本属性 ----------
    report.window_title = _text_of(win)
    try:
        report.class_name = win.class_name()
    except Exception:
        report.class_name = ""
    report.rectangle = _rect(win)

    if not report.window_title:
        report.add("Error", "窗口标题为空")
    if not report.rectangle:
        report.add("Error", "无法读取窗口边界")
    elif report.rectangle["width"] < 400 or report.rectangle["height"] < 300:
        report.add("Warning", f"窗口尺寸偏小: {report.rectangle['width']}x{report.rectangle['height']}")

    try:
        if not win.is_visible():
            report.add("Error", "窗口不可见")
        if not win.is_enabled():
            report.add("Error", "窗口被禁用")
    except Exception:
        pass

    # ---------- 2. 导航 ----------
    for nav_id, label in [
        ("HomeNavItem", "天气"),
        ("CityNavItem", "城市"),
        ("DataSourcesNavItem", "数据源"),
        ("SettingsNavItem", "设置"),
    ]:
        node = _by_id(win, nav_id)
        if node is None:
            report.add("Error", f"缺少导航项: {label} ({nav_id})")
        else:
            report.nav_items.append(label)

    # ---------- 3. 主页面元素 ----------
    city_input = _by_id(win, "CityInput")
    refresh_btn = _by_id(win, "RefreshButton")

    report.has_city_input = city_input is not None
    report.has_refresh_button = refresh_btn is not None

    if city_input is None:
        report.add("Error", "主页面缺少城市输入框 (CityInput)")
    if refresh_btn is None:
        report.add("Error", "主页面缺少刷新按钮 (RefreshButton)")
    # 启动后应用可能还在拉天气（刷新按钮与 IsBusy 绑定），初始禁用属正常，
    # 不报 Warning；下面「功能流程」段有点击前等待就绪的逻辑兜底。

    # ---------- 4. 功能流程：改城市 → 点刷新 → 观察结果 ----------
    if not args.skip_functional and city_input is not None and refresh_btn is not None:
        report.functional_checked = True
        original_city = _text_of(city_input)
        report.city_input_value = original_city

        probe_city = args.probe_city
        try:
            city_input.set_edit_text(probe_city)
            report.city_input_value = _text_of(city_input)
            if report.city_input_value != probe_city:
                report.add("Warning", f"输入框写入后读回不一致: 期望 {probe_city!r}, 实际 {report.city_input_value!r}")
        except Exception as exc:
            report.add("Error", f"无法向城市输入框输入文本: {type(exc).__name__}: {exc}")

        # 启动后应用可能还在拉天气，刷新按钮此时是禁用的——先等它就绪再点
        enabled_deadline = time.time() + args.timeout
        while not refresh_btn.is_enabled() and time.time() < enabled_deadline:
            time.sleep(0.5)
        if not refresh_btn.is_enabled():
            report.add("Warning", f"刷新按钮等待 {args.timeout:.0f}s 后仍禁用，跳过点击")
        else:
            try:
                refresh_btn.invoke()
                report.refresh_invoked = True
            except Exception as exc:
                report.add("Error", f"无法点击刷新按钮: {type(exc).__name__}: {exc}")

        # 轮询：先看是否出现加载态，再等结果稳定
        deadline = time.time() + args.timeout
        seen_text = ""
        stable = 0
        while time.time() < deadline:
            time.sleep(0.5)
            err = _by_id(win, "ErrorText")
            temp = _by_id(win, "TemperatureText")
            err_text = _text_of(err) if err else ""
            temp_text = _text_of(temp) if temp else ""
            progress = _by_id(win, "LoadingProgress") or _by_id(win, "ProgressBar")
            if progress is not None:
                try:
                    if progress.is_visible():
                        report.loading_observed = True
                except Exception:
                    pass

            current = f"{err_text}|{temp_text}"
            if current == seen_text and current.strip("|"):
                stable += 1
                if stable >= 2:
                    break
            else:
                stable = 0
                seen_text = current

        err = _by_id(win, "ErrorText")
        temp = _by_id(win, "TemperatureText")
        hint = _by_id(win, "EmptyHintText")
        report.error_text = _text_of(err) if err else ""
        report.temperature_text = _text_of(temp) if temp else ""
        report.has_empty_hint = hint is not None

        # 判定：温度 / 错误提示 / 空状态引导，至少要有一个反馈，否则说明界面没响应
        if not report.temperature_text and not report.error_text and not report.has_empty_hint:
            report.add("Warning", "刷新后既没有温度、错误提示，也没有空状态引导，界面可能无响应")
        if report.error_text:
            report.add("Info", f"刷新返回业务错误（未配置 API Key 时属预期）: {report.error_text[:80]}")

        # 还原城市，避免污染用户配置
        try:
            if original_city:
                city_input.set_edit_text(original_city)
        except Exception:
            pass

    # ---------- 4.5 数据区块（AQI / 逐小时 / 趋势图 / 生活指数） ----------
    # 注意：Border / Canvas / ItemsControl 在 WinUI 3 里不是 UIA 元素，
    # 挂在它们身上的 AutomationId 不会出现在控件树里。所以锚点一律选 TextBlock 或实际有 peer 的控件。
    temp_node = _by_id(win, "TemperatureText")
    report.temperature_text = _text_of(temp_node) if temp_node else ""

    report.has_current_card = temp_node is not None
    report.has_air_quality = _by_id(win, "AirQualityText") is not None
    report.has_hourly_section = _by_id(win, "HourlyTitle") is not None
    report.has_trend_chart = _by_id(win, "TrendTitle") is not None
    report.has_life_index = _by_id(win, "LifeIndexTitle") is not None
    report.has_cache_hint = _by_id(win, "CacheHintText") is not None

    # 天气图标：WeatherIcon 里重写了 OnCreateAutomationPeer，所以能在 UIA 树里查到，
    # 名字是「天气图标：晴」这种格式，可以顺便验证映射对不对
    icon_current = _by_id(win, "WeatherIconCurrent")
    report.has_icon_current = icon_current is not None
    report.icon_current_name = _text_of(icon_current) if icon_current else ""
    report.has_icon_hourly = _by_id(win, "WeatherIconHourly") is not None
    report.has_icon_forecast = _by_id(win, "WeatherIconForecast") is not None

    trend_range = _by_id(win, "TrendRangeText")
    report.trend_range_text = _text_of(trend_range) if trend_range else ""

    # tier-2 / tier-3 新区块：短临降雨 / 花粉 / 天气预警
    minutely = _by_id(win, "MinutelyRainText")
    report.has_minutely_rain = minutely is not None
    report.minutely_rain_text = _text_of(minutely) if minutely else ""
    pollen = _by_id(win, "PollenText")
    report.has_pollen = pollen is not None
    report.pollen_text = _text_of(pollen) if pollen else ""
    report.has_alerts_card = _by_id(win, "AlertsTitle") is not None
    # AlertsList 是 ItemsControl（无 AutomationPeer），锚点挂在 DataTemplate 的
    # Kind TextBlock 上（AlertKind），按出现次数数条数
    try:
        report.alert_count = sum(
            1 for c in win.descendants()
            if _try_automation_id(c) == "AlertKind"
        )
    except Exception:
        report.alert_count = 0

    if args.expect_data:
        # 演示模式（SKYVIEW_DEMO=1）下这些区块必须全都在，否则说明渲染链路断了
        for flag, label in [
            (report.has_current_card, "当前天气卡片"),
            (report.has_air_quality, "空气质量徽标"),
            (report.has_hourly_section, "逐小时区块"),
            (report.has_trend_chart, "温度趋势区块"),
            (report.has_life_index, "生活指数区块"),
            (report.has_icon_current, "当前天气图标"),
            (report.has_icon_hourly, "逐小时图标"),
            (report.has_icon_forecast, "预报图标"),
            (report.has_minutely_rain, "短临降雨行"),
            (report.has_pollen, "花粉行"),
            (report.has_alerts_card, "天气预警卡片"),
        ]:
            if not flag:
                report.add("Error", f"有数据时应出现「{label}」，但未找到")

        # 演示预报第 3 天是「雨」且概率 80 → 必须推导出至少一条预警
        if report.has_alerts_card and report.alert_count == 0:
            report.add("Error", "天气预警卡片可见但列表为空，推导链路可能断了")
        if report.has_minutely_rain and not report.minutely_rain_text:
            report.add("Error", "短临降雨行可见但文本为空")
        if report.has_pollen and not report.pollen_text:
            report.add("Error", "花粉行可见但文本为空")

        if report.has_trend_chart and not report.trend_range_text:
            report.add("Error", "温度趋势区间文本为空，折线图可能没有绘制")

    # ---------- 5. 数据源页 ----------
    data_sources_nav = _by_id(win, "DataSourcesNavItem")
    if data_sources_nav is not None:
        try:
            data_sources_nav.select()
            time.sleep(args.settings_wait)
            report.data_sources_checked = True
        except Exception as exc:
            report.add("Error", f"无法导航到数据源页: {type(exc).__name__}: {exc}")

    if report.data_sources_checked:
        report.has_api_key_box = _by_id(win, "ApiKeyBox") is not None
        report.has_priority_box = _by_id(win, "PriorityBox") is not None
        report.has_test_button = _by_id(win, "TestConnectionButton") is not None
        report.has_feature_sources_card = _by_id(win, "FeatureSourcesCard") is not None

        provider_list = _by_id(win, "ProviderList")
        if provider_list is not None:
            try:
                items = provider_list.children(control_type="ListItem")
                report.provider_count = len(items)
            except Exception:
                try:
                    report.provider_count = len(provider_list.children())
                except Exception:
                    report.provider_count = 0

        if not report.has_api_key_box:
            report.add("Error", "数据源页缺少 API Key 输入框 (ApiKeyBox)")
        if not report.has_priority_box:
            report.add("Warning", "数据源页缺少优先级输入框 (PriorityBox)")
        if report.provider_count == 0:
            report.add("Error", "数据源页数据源列表为空 (ProviderList)")
        elif report.provider_count < 3:
            report.add("Warning", f"数据源条目仅 {report.provider_count} 个（预期 3 个以上）")
        if not report.has_test_button:
            report.add("Error", "数据源页缺少「测试连接」按钮 (TestConnectionButton)")
        if not report.has_feature_sources_card:
            report.add("Error", "数据源页缺少「功能数据源分配」卡片 (FeatureSourcesCard)")

    # ---------- 6. 设置页（只剩显示偏好；保存按钮已随实时保存移除） ----------
    settings_nav = _by_id(win, "SettingsNavItem")
    if settings_nav is not None:
        try:
            settings_nav.select()
            time.sleep(args.settings_wait)
            report.settings_checked = True
        except Exception as exc:
            report.add("Error", f"无法导航到设置页: {type(exc).__name__}: {exc}")

    if report.settings_checked:
        report.has_unit_toggle = _by_id(win, "UnitToggle") is not None
        report.has_auto_refresh_box = _by_id(win, "AutoRefreshBox") is not None
        report.has_version_text = _by_id(win, "VersionText") is not None

        if not report.has_unit_toggle:
            report.add("Error", "设置页缺少温度单位开关 (UnitToggle)")
        if not report.has_auto_refresh_box:
            report.add("Error", "设置页缺少自动刷新间隔 (AutoRefreshBox)")
        if not report.has_version_text:
            report.add("Warning", "设置页缺少版本号 (VersionText)")

    # 「保存」按钮已随实时保存移除：两个页面都不该再有它
    if _by_id(win, "SaveSettingsButton") is not None:
        report.add("Error", "仍存在「保存设置」按钮（实时保存后应移除）")

    # 回到天气页，保持界面状态干净
    home_nav = _by_id(win, "HomeNavItem")
    if home_nav is not None:
        try:
            home_nav.select()
        except Exception:
            pass

    if proc is not None and not args.keep_running:
        try:
            proc.terminate()
        except Exception:
            pass

    return report


def print_text(report: Report) -> None:
    print("=== pywinauto 功能测试报告 (UIA backend) ===")
    print(f"应用：{report.app}")
    print(f"窗口标题：{report.window_title}")
    print(f"类名：{report.class_name}")
    print(f"进程 ID：{report.process_id}")
    if report.rectangle:
        r = report.rectangle
        print(f"边界：({r['left']},{r['top']}) {r['width']}x{r['height']}")
    print()

    print("--- 导航 ---")
    print(f"导航项：{report.nav_items}")
    print()

    print("--- 主页面 ---")
    print(f"有城市输入框：{report.has_city_input}")
    print(f"有刷新按钮：{report.has_refresh_button}")
    if report.functional_checked:
        print(f"输入城市：{report.city_input_value!r}")
        print(f"已点击刷新：{report.refresh_invoked}")
        print(f"观察到加载态：{report.loading_observed}")
        print(f"温度文本：{report.temperature_text!r}")
        print(f"错误提示：{report.error_text[:100]!r}")
        print(f"空状态引导：{report.has_empty_hint}")
    else:
        print("功能流程：已跳过")
    print()

    print("--- 数据区块 ---")
    print(f"当前天气卡片：{report.has_current_card}")
    print(f"空气质量徽标：{report.has_air_quality}")
    print(f"逐小时区块：{report.has_hourly_section}")
    print(f"温度趋势区块：{report.has_trend_chart}（{report.trend_range_text}）")
    print(f"生活指数区块：{report.has_life_index}")
    print(f"缓存提示：{report.has_cache_hint}")
    print(f"天气图标：当前={report.has_icon_current}（{report.icon_current_name}） 逐小时={report.has_icon_hourly} 预报={report.has_icon_forecast}")
    print()

    print("--- 数据源页面 ---")
    if report.data_sources_checked:
        print(f"有 API Key 输入框：{report.has_api_key_box}")
        print(f"有优先级输入框：{report.has_priority_box}")
        print(f"有测试连接按钮：{report.has_test_button}")
        print(f"有功能数据源分配卡片：{report.has_feature_sources_card}")
        print(f"数据源条目数：{report.provider_count}")
    else:
        print("未检查")
    print()

    print("--- 设置页面 ---")
    if report.settings_checked:
        print(f"有温度单位开关：{report.has_unit_toggle}")
        print(f"有自动刷新间隔：{report.has_auto_refresh_box}")
        print(f"有版本号：{report.has_version_text}")
    else:
        print("未检查")
    print()

    errors = [i for i in report.issues if i["level"] == "Error"]
    warnings = [i for i in report.issues if i["level"] == "Warning"]
    infos = [i for i in report.issues if i["level"] == "Info"]

    print(f"--- 问题 (Error {len(errors)} / Warning {len(warnings)} / Info {len(infos)}) ---")
    for issue in report.issues:
        print(f"  [{issue['level']}] {issue['message']}")
    if not report.issues:
        print("  无问题")
    print("=== 检查完成 ===")


def main() -> int:
    parser = argparse.ArgumentParser(description="SkyView pywinauto 功能测试")
    parser.add_argument("--pid", type=int, help="已运行的 SkyView 进程 PID")
    parser.add_argument("--launch", action="store_true", help="自动启动 SkyView 后测试")
    parser.add_argument("--app-path", help="SkyView.exe 路径（配合 --launch）")
    parser.add_argument("--probe-city", default="上海", help="功能测试时输入的城市，默认 上海")
    parser.add_argument("--timeout", type=float, default=15.0, help="连接/等待超时秒数，默认 15")
    parser.add_argument("--settings-wait", type=float, default=1.5, help="切到设置页后等待秒数")
    parser.add_argument("--skip-functional", action="store_true", help="跳过改城市/点刷新的交互流程")
    parser.add_argument(
        "--expect-data",
        action="store_true",
        help="要求数据区块（当前天气/AQI/逐小时/趋势图/生活指数）必须存在。配合 SKYVIEW_DEMO=1 使用",
    )
    parser.add_argument("--keep-running", action="store_true", help="测试后不关闭由 --launch 启动的进程")
    parser.add_argument("--json", action="store_true", help="输出 JSON 报告")
    args = parser.parse_args()

    if not args.pid and not args.launch:
        parser.error("必须提供 --pid 或 --launch")

    report = run(args)

    if args.json:
        payload = {
            k: v for k, v in report.__dict__.items() if k not in {"exit_code"}
        }
        payload["issue_count"] = len(report.issues)
        payload["error_count"] = sum(1 for i in report.issues if i["level"] == "Error")
        payload["is_valid"] = report.exit_code == 0
        print(json.dumps(payload, ensure_ascii=False, indent=2))
    else:
        print_text(report)

    return report.exit_code


if __name__ == "__main__":
    sys.exit(main())
