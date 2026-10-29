# SkyView 构建与运行说明

## 一、环境要求

| 组件 | 版本 | 说明 |
|------|------|------|
| 操作系统 | Windows 10 1809+ / Windows 11 | WinUI 3 目标平台 |
| .NET SDK | 10.0.402 | 已验证 |
| Windows SDK 引用包 | `Microsoft.Windows.SDK.NET.Ref` 10.0.26100.57 | 通过 `<WindowsSdkPackageVersion>` 指定 |
| Windows App SDK | 1.8.260921001 | NuGet 自动还原，**自包含部署**，目标机无需预装运行时 |
| Python | 3.9+ | 仅 UI 布局检查脚本需要 |

## 二、解决方案结构

```
SkyView.sln
├── src/SkyView.Core/      # 非 UI 类库 (net10.0)：模型 / 数据源 / 服务 / ViewModel / 存储
├── src/SkyView/           # WinUI 3 应用 (net10.0-windows10.0.19041.0)
├── tests/
│   ├── SkyView.UnitTests/      # xUnit + Moq
│   ├── SkyView.ComponentTests/ # xUnit + Moq
│   └── SkyView.UITests/        # xUnit 冒烟测试
└── tools/
    ├── run_all.py              # 一键验证：构建 → 发布 → 测试 → UI 检查
    ├── check_ui_layout.py      # UI 布局只读检查（uiautomation）
    ├── check_ui_pywinauto.py   # UI 交互功能测试（pywinauto）
    ├── capture_tiers.py        # 逐页截图取证（写 docs/shots/）
    ├── verify_realdata.py      # 真实数据链路验证（Open-Meteo 兜底链）
    ├── verify_wttr.py          # 真实数据链路验证（临时置顶 wttr.in）
    ├── verify_feature_source.py  # per-feature 选源真机验证
    └── verify_settings_live.py   # 实时保存回归（单位 + 指派源，无保存点击）
```

**为什么拆出 `SkyView.Core`**：业务逻辑（含 ViewModel）不引用 WinUI / Windows App SDK，
因此单元测试和组件测试可以在**没有图形会话、没有 WinAppSDK 运行时**的环境里正常跑完，
不会因为加载 `Microsoft.UI.Xaml.dll` 而让测试宿主直接崩溃。

## 三、关键构建配置

### 主项目 src/SkyView/SkyView.csproj

```xml
<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
<UseWinUI>true</UseWinUI>
<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>  <!-- 自带 WinAppSDK 运行时 -->
<WindowsPackageType>None</WindowsPackageType>                  <!-- 未打包，走自动引导 -->
<SelfContained>true</SelfContained>                            <!-- 自带 .NET 运行时 -->
<WindowsSdkPackageVersion>10.0.26100.57</WindowsSdkPackageVersion>
<Platform>x64</Platform>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<AllowUnsafeBlocks>true</AllowUnsafeBlocks>
```

- `WindowsPackageType=None`（未打包）→ `Windows.Storage.ApplicationData.Current` **不可用**，
  本地数据统一放 `%LOCALAPPDATA%\SkyView\`（见 `SkyView.Core/Services/AppPaths.cs`）。
- `WindowsAppSDKSelfContained=true` → publish 输出包含 WinAppSDK 运行时（约 10 个 `WindowsAppRuntime*` 文件），
  目标机**不需要**安装 Windows App SDK 运行时。
- `SelfContained=true` → publish 输出包含 .NET 运行时（`coreclr.dll` / `hostfxr.dll` /
  `System.Private.CoreLib.dll` 等）。**必须显式写**：从 .NET 6 起 `dotnet publish -r <rid>`
  不再隐含自包含，默认仍是 framework-dependent，目标机没装 .NET Desktop Runtime 就弹
  「You must install .NET Desktop Runtime」。两个 `SelfContained` 是两件事，别搞混：
  一个管 WinAppSDK，一个管 .NET 本体。
- `Platform=x64` + `RuntimeIdentifier=win-x64` 是自包含部署的必需项，缺了会报 `NETSDK1083`。

代价是体积：全自包含 publish 约 **219 MB / 443 个文件**（其中 .NET 运行时占大头）。
只想内部开发机跑可以临时关掉 `SelfContained`，分发版务必打开。

### 测试项目

测试项目**不需要** `UseWinUI`，只引用 `SkyView.Core`：

```xml
<TargetFramework>net10.0</TargetFramework>
<ProjectReference Include="..\..\src\SkyView.Core\SkyView.Core.csproj" />
```

## 四、构建 / 发布命令

> 日常验证直接用 **[五、一键验证脚本](#五一键验证脚本推荐)** 更省事，下面这些是它内部调用的原始命令。

```bash
# 构建主应用（产物：src/SkyView/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/）
dotnet build src/SkyView/SkyView.csproj -c Debug

# 发布（Release，产物在 .../win-x64/publish/，可直接分发运行）
dotnet publish src/SkyView/SkyView.csproj -c Release
```

> ⚠️ **不要用 `dotnet publish -o <自定义目录>`**。
> 使用自定义输出目录时，`dotnet publish` 不会复制应用自身的 `SkyView.pri`（XAML 资源索引），
> 运行时会立刻在 `InitializeComponent()` 抛 `XamlParseException`（进程退出码 `0xC000027B`）。
> 项目已在 csproj 里加了 `CopyPriToPublish` 目标做兜底，但**默认发布路径最稳**。

## 五、一键验证脚本（推荐）

`tools/run_all.py` 把「构建 → 发布 → 三层测试 → 产物完整性 → 两个 UI 检查」串成一条命令，
最后输出汇总表。日常改完代码跑这一个就够了。

```bash
# 全流程（约 1.5~2 分钟）
python tools/run_all.py

# 只重跑测试和产物检查，不重新发布
python tools/run_all.py --skip-publish

# 没有图形会话（远程/CI）时跳过 UI 检查
python tools/run_all.py --skip-ui

# 只跑两个 UI 检查
python tools/run_all.py --only-ui

# 换配置
python tools/run_all.py --configuration Debug
```

实测输出：

```
步骤                          结果          用时
----------------------------------------------------------------------
构建解决方案                  ✓ 通过       36.2s
发布自包含版本                ✓ 通过       26.3s
单元测试                      ✓ 通过        6.3s  78 用例通过
组件测试                      ✓ 通过        6.2s  12 用例通过
UI 冒烟测试                   ✓ 通过        5.7s  4 用例通过
发布产物完整性                ✓ 通过        0.0s  343 文件，关键文件齐全
UI 布局检查 (uiautomation)    ✓ 通过        9.7s  0 个问题
UI 功能检查 (pywinauto)       ✓ 通过       15.3s  Error 0 / Warning 0
----------------------------------------------------------------------

共 8 步：通过 8，失败 0，跳过 0，总用时 105.8s
全部通过。
```

几个设计点：

- **UI 检查自动带 `SKYVIEW_DEMO=1`**。没有 API Key 时真实数据源必然失败，
  逐小时/趋势图/生活指数不会渲染，断言无从下手；演示数据源提供稳定数据，让 UI 测试与网络解耦。
- **`发布产物完整性` 这一步专治 `SkyView.pri` 缺失**。它只读地检查
  `SkyView.exe` / `SkyView.pri` / `SkyView.Core.dll` / `appsettings.json` 是否都在，
  0 秒完成。这个坑的症状是「像缺运行包」，靠肉眼看目录很容易漏。
- **Python 解释器有要求**。脚本用 `sys.executable` 去跑 tools 下两个 UI 脚本，
  所以**跑本脚本的解释器**必须装好 `uiautomation` 与 `pywinauto`。
  本机 `C:\python\python.exe` 两个都有，建议用它：
  ```bash
  C:/python/python.exe tools/run_all.py
  ```
  预检发现缺模块时不会硬失败，而是把 UI 两步标记为「跳过」并列出缺什么，
  同时退出码为 1（有步骤被跳过 ≠ 全量通过）。
- 退出码 `0` 仅代表全部步骤真的通过；有任何失败或跳过都返回 `1`，可直接接 CI。

## 六、四个曾导致「界面启动不了」的真实原因（已修复）

按发现顺序记录，避免以后重复踩坑。

### 1. XAML 里使用了不存在的 `Symbol` 枚举值

`MainWindow.xaml` 的 `NavigationViewItem Icon="Place"` —— `Place` **不在 WinUI 3 的 `Symbol` 枚举里**。
XAML 解析器在 `MainWindow.InitializeComponent()` 直接抛异常，而 `OnLaunched` 里没有捕获，
于是变成未处理异常 → `0xC000027B`，进程秒退、窗口从未出现。

已改用合法图标（`Home` / `Map` / `Setting`）。**这是"启动不了"的真正根因。**

### 2. 发布目录缺少 `SkyView.pri`

`dotnet publish` 不复制 `<AssemblyName>.pri`（XAML 的 XBF 资源都在里面），
缺了它启动时报同样的 `XamlParseException`，但 stack 落在 `MainWindow..ctor()`。
已在 csproj 增加 `CopyPriToPublish` 目标，在 Publish 之后补复制。

### 3. `IMemoryCache` 未注册

`WeatherService` 依赖 `IMemoryCache`，但 DI 里没注册，`MainPage` 构造时抛异常。
已在 `App.xaml.cs` 补 `services.AddMemoryCache()`。

### 4. .NET 运行时没有自包含（就是那句「需要安装运行包」）

症状是一个 **556×222 的错误对话框**，标题是 `SkyView.exe`，正文：

> You must install .NET Desktop Runtime to run this application.

这个框极具误导性：它没有崩溃、没有 `crash.log`、进程还活着，只是窗口不是主界面。
排查时如果用 `EnumWindows` 只看「有没有可见窗口」，会误判成「应用起来了」；
如果用 pywinauto 等 `title="SkyView"`，则会一直超时，因为框的标题是 `SkyView.exe`。

根因：只设了 `WindowsAppSDKSelfContained=true`，漏了 `SelfContained=true`。
.NET 6 起 `dotnet publish -r win-x64` **不再**隐含自包含，publish 目录里 141 MB 全是
WinAppSDK 和依赖包，`coreclr.dll` / `hostfxr.dll` 一个都没有 → apphost 去找系统装的
.NET Desktop Runtime，找不到就弹这个框。

修法：csproj 加 `<SelfContained>true</SelfContained>`。之后 publish 是 219 MB / 443 个文件，
把 `DOTNET_ROOT` 删掉、`PATH` 里也不带 dotnet，仍然 2 秒内出主窗口。

**判别方法**：`publish` 目录里有没有 `coreclr.dll`。有 = 自包含；没有 = 依赖系统运行时。

### 排查手段：崩溃日志

`App` 构造函数里挂了 `UnhandledException`，崩溃栈写到：

```
%LOCALAPPDATA%\SkyView\crash.log
```

以后再遇到"双击没反应"，**先看这个文件**，比翻事件日志快得多。
注意：第 4 类问题（缺运行时）**不会**产生 crash.log，因为它压根没进托管代码。

## 七、测试结果（已验证）

| 测试项目 | 通过 | 失败 | 总计 |
|----------|------|------|------|
| SkyView.UnitTests | 228 | 0 | 228 |
| SkyView.ComponentTests | 27 | 0 | 27 |
| SkyView.UITests | 4 | 0 | 4 |
| **合计** | **259** | **0** | **259** |

```bash
dotnet test tests/SkyView.UnitTests/SkyView.UnitTests.csproj
dotnet test tests/SkyView.ComponentTests/SkyView.ComponentTests.csproj
dotnet test tests/SkyView.UITests/SkyView.UITests.csproj
```

（或直接 `python tools/run_all.py`，会连构建和 UI 检查一起跑。）

覆盖内容：
- 天气状况文本映射（中/英、模糊匹配、未知降级）
- `WeatherFormat` 单位与数字格式化、温度换算、AQI 分级、缓存时长文案
- `WeatherViewModel` 刷新成功/失败/加载态/命令可用性
- 空状态：首次进入不显示 `0°C`、成功后退场、失败时不误报空、刷新失败保留旧数据
- 离线缓存：缓存写入/读回、损坏文件回退、缓存恢复时打标记、刷新成功后清除标记
- 单位切换：`ApplyUnit` 切 °F 后温度与趋势区间同步换算
- `PreferencesStore` 往返/损坏回退/自动刷新间隔归一化
- `ProviderConfigItem` 的 Key 掩码、留空不改、清空语义
- `WeatherService` 多源按优先级降级、全部失败时的异常、缓存命中
- 各数据源的逐小时/空气质量/生活指数**经接口调用**的映射正确性
  （刻意通过 `IWeatherProvider` 而不是具体类型调用——接口默认实现曾导致派生类方法永不执行）
- `WttrProvider` 的 stub 测试：字符串数值解析、中午描述选取、16 方位风向、日出日落
  AM/PM 归一、逐小时"时间×100"两种量纲、过去日期过滤、URL 空格编码（6 个用例）
- 按功能分配数据源：指派源只服务其功能、未指派走自动链、指派源失败回落优先级链、
  指派的源不存在等同自动、`FeatureSources` 经 `preferences.json` 往返（6 个用例）
- 缓存键含数据源签名：切换指派源 / 改启用链后，**新鲜的缓存也必须绕开**，
  不能出现"保存后来源标注 10 分钟不换"（2 个回归用例）
- 实时保存：显示类改动自动落盘不发刷新信号、数据源类改动（API Key / 优先级 /
  启用 / 按功能分配）自动落盘**并**发刷新信号、Persist 回写不触发二次保存（防反馈环）、
  构造期不保存、`PersistNow` 立即冲刷与空改动无操作（7 个用例）
- tier-1 新指标：Open-Meteo 的 uv_index/precipitation/cloud_cover/dew_point_2m/wind_gusts_10m
  可空解析（0 与 null 必须区分）、hourly pressure_msl 与 time 等长对齐、wttr.in 的
  current uvIndex + hourly 最近时刻条取 DewPointC/WindGustKmph/hourly pressure（9 个用例）；
  `WeatherFormat.UvCategory`/`UvLevel`/`PressureTrend` 边界值（26 个用例）；
  ViewModel 派生文本、数据缺失全隐藏、露点随单位重算（6 个用例）
- tier-2 新指标：Open-Meteo daily 拉满 15 天（forecast_days=15）并解析
  precipitation_probability_max / sunrise / sunset（旧版三要素响应不崩）；minutely_15
  15 分钟粒度算「未来 2 小时会不会下雨」——首槽阈值 0.1mm、连续槽累计持续时间、
  空数组/缺字段按"无数据"（null）而非"无雨"处理（6 个用例）；
  `WeatherFormat.MinutelyRainSummary` 三种结局文案 + null 抛参（5 个用例）；
  ViewModel：有数据显示结论、无数据整行隐藏、预报标题随天数动态（4 个用例）
- tier-3 新指标：`WeatherAlertEngine.Derive` 从 daily/current 按国内阈值本地推导预警——
  降水概率 ≥60 黄 / ≥80 橙、高温 ≥35°C 橙 / ≥37°C 红、寒潮 ≤-10°C、阵风 ≥62 km/h（8 级）大风；
  同日同类去重保高级、按日期近→远排序、封顶 5 条、四级 `LevelText`、Reason 随单位换算（13 个用例）；
  `WeatherFormat.PollenCategory`/`PollenLevel` 四档边界（≤20 低 / ≤50 中 / ≤100 高 / 更高 极高）（14 个用例）；
  ViewModel：预警刷新后生成、无触发为空集、切单位重推导（Reason 含温度文本）、
  花粉有数据显示/无数据整行隐藏、Peak 取草/树两类最高（6 个用例）；
  Open-Meteo Air Quality API 花粉解析（树类取桦/豚草/桤/橄榄四变量最大值）、
  五变量全 null → null、接口失败不影响 current（3 个用例）
- 天气图标分类：中文/英文、强度词、优先级（雷 > 雪 > 暴雨 > 雨）、降水概率升级、兜底
- 发布产物完整性（`SkyView.exe` / `SkyView.pri` / `SkyView.Core.dll` / `appsettings.json`）

## 八、数据与配置位置

| 内容 | 路径 | 说明 |
|------|------|------|
| 加密的 API Key / 数据源设置 | `%LOCALAPPDATA%\SkyView\settings.dat` | **DPAPI 加密**（`ProtectedData`，CurrentUser 范围） |
| 城市列表 | `%LOCALAPPDATA%\SkyView\cities.json` | 纯文本，非敏感 |
| 崩溃日志 | `%LOCALAPPDATA%\SkyView\crash.log` | 未处理异常栈 |
| 数据源默认定义 | 应用目录 `appsettings.json` | 首次运行的默认清单 |

> 换机器或重装系统后 `settings.dat` 解不开（DPAPI 与用户账户绑定），属正常现象，重新填 Key 即可。

## 九、UI 布局检查（Python uiautomation）

`tools/check_ui_layout.py` 基于 PyPI 的 **uiautomation**（UIA3）做布局校验。

### 安装

```bash
pip install uiautomation
```

### 用法

```bash
# 推荐：连到已运行的进程（最准确）
python tools/check_ui_layout.py --pid 12345

# 启动并检查
python tools/check_ui_layout.py --launch

# JSON 报告
python tools/check_ui_layout.py --pid 12345 --json > layout-report.json
```

### 窗口定位策略

1. `--pid`：遍历顶级窗口匹配 `ProcessId`，带重试（应对窗口尚未创建）
2. `--launch`：先按启动进程 PID 定位，失败回退到 `Name="SkyView"` 并用子元素特征（城市输入框、刷新按钮）校验
3. uiautomation 的根节点枚举失效时，自动回退到 Win32 `EnumWindows`

### 实测输出（Release publish 版）

```
=== UI Automation 布局检查报告 (Python uiautomation) ===
应用：SkyView
窗口标题：SkyView
类名：WinUIDesktopWin32WindowClass
进程 ID：9508
边界：(182,182) 1000x720
可见：True，启用：True

--- 导航视图 ---
导航项数量：4
  导航项 '天气': {'left': 194, 'top': 259, 'width': 312, 'height': 36}
  导航项 '城市': {...}
  导航项 '数据源': {...}
  导航项 '设置': {...}

--- 主页面 ---
有城市输入框：True
有刷新按钮：True
有进度条：False          <- 非加载态时进度条隐藏，属正常
TextBlock 数量：87

--- 数据源页面 ---
已导航检查：True
有 API Key 输入框：True
有优先级 NumberBox：True
有测试连接按钮：True
数据源条目数：4

--- 设置页面 ---
已导航检查：True
有温度单位开关：True
有自动刷新间隔输入框：True
有版本号：True
残留「保存设置」按钮（应为 False）：False

--- 问题 (1) ---
  [WARN]  刷新按钮当前不可用（可能正在加载）
=== 检查完成 ===
```

（启动后紧接着检查时应用可能还在拉天气，刷新按钮短暂不可用只记 WARN，不计 Error。）

退出码：有 `Error` 级问题时为 `1`，否则为 `0`。已接入 CI 判定。

### 说明

- 脚本会自动点击导航项分别进入「数据源」页（API Key 输入框 / 优先级 NumberBox /
  测试连接按钮 / 数据源条目数）与「设置」页（单位开关 / 自动刷新间隔 / 版本号），
  并断言设置页**没有**残留的保存按钮。
- 进度条在非加载态为 `Collapsed`，报告为 `False` 不计入问题。

## 十、UI 功能测试（pywinauto）

`tools/check_ui_pywinauto.py` 用 **pywinauto（uia 后端）** 做**真实交互**测试 ——
和第九节的只读布局测量互补：这个会真的输入文字、点按钮、切页面。

### 结论：pywinauto 完全可用于 WinUI 3

WinUI 3 用标准 UIA（Microsoft UI Automation）暴露控件，pywinauto 的 `uia` 后端能完整驱动。
实测可用的操作：

| 操作 | 方法 | 实测结果 |
|------|------|---------|
| 连接进程 | `Application(backend="uia").connect(process=pid)` | OK |
| 取窗口 | `app.window(title="SkyView")` | OK（`WindowSpecification`） |
| 按 AutomationId 查找 | `spec.child_window(auto_id="CityInput")` | OK |
| 读输入框内容 | `get_value()` / `window_text()` | OK（读出 `北京`） |
| 写入输入框 | `set_edit_text("上海")` | OK（值真的变了） |
| 点击按钮 | `invoke()` | OK（刷新真的触发） |
| 切换导航页 | `select()` | OK（切到设置页成功） |
| 判断可用性 | `is_enabled()` | OK |
| 遍历全部控件 | `wrapper.descendants()` + `automation_id()` | OK（拿到 16 个 Id） |

### 安装

```bash
pip install pywinauto
```

### 用法

```bash
# 自动启动 + 完整功能流程（布局 + 改城市 + 点刷新 + 进设置页）
python tools/check_ui_pywinauto.py --launch

# 连到已运行的进程
python tools/check_ui_pywinauto.py --pid 12345

# 只要布局，不触发交互
python tools/check_ui_pywinauto.py --launch --skip-functional

# Demo 数据下断言 tier-2/3 新功能行存在（短临降雨 / 花粉 / 预警卡片，卡片可见列表不能为空）
python tools/check_ui_pywinauto.py --launch --expect-data

# JSON 报告
python tools/check_ui_pywinauto.py --launch --json > report.json
```

### 它会做什么

1. 连接窗口，校验标题 / 类名 / 边界 / 可见 / 启用
2. 校验 4 个导航项（`HomeNavItem` / `CityNavItem` / `DataSourcesNavItem` / `SettingsNavItem`）
3. 校验主页面 `CityInput`、`RefreshButton`
4. **功能流程**：把城市改成 `--probe-city`（默认 `上海`）→ 等刷新按钮就绪 → 点刷新 →
   轮询等待结果稳定 → 读取 `ErrorText` / `TemperatureText` / `EmptyHintText` → **还原原城市**
5. 导航到数据源页，校验 `ApiKeyBox` / `PriorityBox` / `TestConnectionButton` /
   `FeatureSourcesCard` / `ProviderList` 及条目数（应为 4）
6. 全局断言 `SaveSettingsButton` **不存在**（实时保存改造后保存按钮已移除）
7. 导航到设置页，校验 `UnitToggle` / `AutoRefreshBox` / `VersionText`
8. 回到天气页，退出
9. 加 `--expect-data` 时（配 `SKYVIEW_DEMO=1` 的稳定演示数据）：校验「短临降雨」行与
   「花粉」行有值、预警卡片可见且条数 ≥1、各卡来源标注非空

判定规则：温度 / 错误提示 / 空状态引导三者至少有其一，否则报"界面无响应"。
未配置 API Key 时返回的业务错误记为 `Info` 而非 `Error`（属预期）。

### pywinauto 在 WinUI 3 上的 9 个坑（脚本已绕过）

1. `app.windows()[0]` 返回的是 **`UIAWrapper`**，**没有 `child_window()`** 方法 ——
   必须用 `app.window(title="SkyView")` 拿到 `WindowSpecification` 才能按 auto_id 查找。
   （最初按 `windows()[0].child_window(...)` 写，全部返回 MISS，就是这个原因。）
2. `app.window(process=pid)` 会抛 `KeyError`：`connect(process=...)` 已绑定进程，不能再传 `process`。
3. **AutomationId 必须显式写在 XAML 里**才有。`ListView.ItemTemplate` 内部的控件
   （如每行的启用 CheckBox）默认没有 Id，只能靠 `descendants()` 按控件类型统计。
4. 深层控件用 `child_window` 有时不稳，改用 `descendants()` + `automation_id()` 过滤更可靠。
5. **WinUI 的 UIA 树不含 ScrollViewer 视口外的元素。** 默认窗口高 720px 时「温度趋势」卡
   折在可视区外，`child_window(auto_id="ForecastSourceText")` 直接报元素不存在——
   不是绑定坏了，是元素压根没被 UIA 暴露。读之前要么把窗口拉高（`MoveWindow`），
   要么用 `ScrollPattern` 把目标区域滚进视口（`capture_tiers.py` 里已有现成滚动逻辑）。
   `verify_feature_source.py` 采用起窗后 `MoveWindow(…, 1000, 1000, …)` 的做法。
6. **报错对话框的标题可能恰好是 "SkyView"**。framework-dependent 包在目标机缺
   .NET 运行时时，apphost 弹的「.NET location: Not found」对话框标题就是应用名，
   `app.window(title="SkyView")` 会把它当主窗口等到超时，读取也全是空。
   判别方法：等窗口之外再校验子元素特征（城市输入框 / 刷新按钮），或者干脆看
   publish 目录里有没有 `coreclr.dll`。
7. **单实例互斥会让复现脚本"起不来"**。重复启动时第二个实例激活旧窗口后自己退出，
   脚本连的新进程秒退，表现为 30s 超时无窗口；且旧实例窗口可能已收进托盘，
   `EnumWindows` 根本枚举不到。跑复现脚本前先 `tasklist` 清掉旧实例。
8. **`AutomationProperties.AutomationId` 挂在没有 AutomationPeer 的控件上等于没挂。**
   WinUI 的 `Border` / `Panel` / `ItemsControl` 这类布局/容器元素在 UIA 树里不产生 peer，
   挂在它们身上的 AutomationId 不会出现在树里，`child_window(auto_id=...)` 永远找不到——
   表现为"控件明明在界面上，自动化却报缺失"。锚点必须挂 `TextBlock` 等真实控件
   （`DataSourcesPage` 的 `FeatureSourcesCard`、主页预警卡 `AlertsList`
   都是这么修的；预警卡的锚点在 `DataTemplate` 内的 `TextBlock` 上，用
   `descendants()` 扫描计数）。
9. **`child_window()` 返回的 `WindowSpecification` 是延迟解析的。** 调用它不查 UIA 树，
   `spec is None` 恒为 False——「断言某元素已移除」的检查会永远失败。
   必须调 `spec.exists()` 真正查一次树再判断。

### 三个工具怎么选

| | `check_ui_layout.py` (uiautomation) | `check_ui_pywinauto.py` (pywinauto) | `capture_tiers.py` |
|---|---|---|---|
| 定位 | 只读布局测量 | 交互式功能测试 | 逐页截图取证 |
| 输出 | 控件边界坐标、控件计数 | 交互结果、状态文本 | `docs/shots/*.png` |
| 改界面 | 不改 | 会改（并自动还原） | 会改（切单位、滚页面） |
| 适合 | CI 里的布局回归、视觉走查 | 验证"点了到底有没有反应" | 人工看效果、写文档配图 |
| 依赖 | `pip install uiautomation` | `pip install pywinauto` | 两个都要 + `pillow` |

> 三个脚本都会自动优先使用 **Release publish** 的 exe，找不到才退回 Debug 构建。
>
> 想一次把前两个都跑掉：`python tools/run_all.py --only-ui`（会自动带 `SKYVIEW_DEMO=1`）。

`capture_tiers.py` 会依次截：天气页顶屏 → 温度趋势 → 预报/生活指数 → 数据源页 →
设置页 → 实时保存后 → 切换单位后，并顺手校验「切单位后温度文本必须变化」、
「设置页/数据源页均无保存按钮」，任一不满足就返回 1。

滚动走 UIA 的 `ScrollPattern`（`Scroll(NoAmount, SmallIncrement)` 小步推进，
直到目标标题完整进入可视区）。**不要用注入滚轮**：

- 滚轮要求窗口在前台、鼠标真的落在窗口上，后台跑会静默无效，很难 debug
- 逐小时那条横向滚动区会把纵向滚轮吃掉，鼠标落在那儿就滚不动页面

页面 ScrollViewer 是**有** ScrollPattern 的（`class_name == "ScrollViewer"`
且 `CurrentVerticallyScrollable == True`），按 class_name 过滤就能和逐小时那条
横向滚动器区分开。注意 `CurrentCanScroll` 在这个场景恒为 False，别拿它判断。

还有个前置条件：脚本启动时必须调 `SetProcessDpiAwareness(2)`。
否则进程是 DPI-unaware 的，`GetWindowRect` 给的是逻辑像素，而 `SetCursorPos`
按物理像素工作，125% 缩放下两者差 25% —— **鼠标会落到别的窗口上，点击全打在空处**。
必须在任何窗口查询之前调用，模块导入时就要设好。

### 截图为什么用 PrintWindow 而不是 ImageGrab

`ImageGrab.grab(bbox=窗口矩形)` 是"按坐标去屏幕上抠一块"，窗口只要不在最前、
或者周围有一像素误差，就会把桌面、任务栏、别的窗口边角一起截进来。
改成让窗口自己往内存 DC 上画：

```python
user32.PrintWindow(hwnd, mdc, PW_RENDERFULLCONTENT)   # flag = 0x2
```

- `PW_RENDERFULLCONTENT` 不能省。WinAppSDK 走 DirectComposition 合成，
  不带这个 flag 走的是老的 `WM_PRINT` 路径，截出来是纯黑或纯白。
- 拿到 `GetDIBits` 的 BGRA 缓冲后，PIL 用 `Image.frombuffer(..., "raw", "BGRX", ...)`
  直接读成 RGB，顺手跳过 alpha。
- `biHeight` 传负数 = top-down，省一次垂直翻转。
- **所有返回句柄的 GDI 函数都要显式写 `restype`**。ctypes 默认按 `c_int` 处理，
  64 位下 `CreateCompatibleDC` 的句柄会被截断成垃圾值，报错还很难懂。
- 仍然保留了 `ImageGrab` 兜底：`PrintWindow` 返回 0 时打一行提示再退回屏幕抓取。

**它的效果**：换掉之后截图边缘不再有桌面碎片，6 张全部 1000×720、内容完整，
`docs/shots/01-天气页.png` 里左侧导航三项、当前天气卡片、逐小时图标行都干净。

**它已经抓到过东西**：逐小时列表的第 8 项被切在卡片右边界上
（`15:00` 只剩 12px 宽，`16:00` 之后因为裁剪完全没渲染）。功能上可以横向滚动，
但视觉上像排版破了。

**已按方案 3 修复**：`MainPage.xaml.cs` 的 `LayoutHourly()` 监听 `HourlyScroller.SizeChanged`，
按可用宽度算列数（`n = 可用宽 / 80`，夹在 4~9），把每个容器宽度设成 `可用宽 / n - 间距`，
右边缘永远落在项与项之间。容器尚未生成（刚切数据源）时用 `DispatcherQueue.TryEnqueue`
下一帧补算一次。

### 真实数据链路验证（pywinauto + PrintWindow）

`tools/verify_realdata.py` 与 `tools/verify_wttr.py` 在前面所有检查**不带 `SKYVIEW_DEMO`**
的前提下真连互联网取数，验证"真实数据源"这条链路（前面几节的 UI 检查都带 `SKYVIEW_DEMO=1`，
与应用市场发布形态不同，必须再补一轮免 Key 兜底链的实测）。

```bash
python tools/verify_realdata.py   # 默认配置：Open-Meteo 兜底链 + 城市页瀑布流
python tools/verify_wttr.py       # 自包含：临时把 wttr.in 置顶（自动备份/还原 appsettings.json）
```

`verify_wttr.py` 自己改**发布目录**的 `appsettings.json`（Wttr 置顶、暂时关掉其它源），
跑完还原，源码配置不受影响。验证点：主页温度是真实数值、来源标签为 `Wttr`、
截图存档、进程无崩溃无 crash.log。

**踩过的坑：温度合理性断言要按单位判断。** 断言写成 `-90 < val < 60`（摄氏制常识范围），
但界面显示 **64.4°F**（=18°C，完全正常）却被判失败——59°F 能过纯属侥幸。
正确做法：先按 `°F/°C` 换算成摄氏再比范围。同理，来源标签读出来是 `Wttr` 而不是
`wttr.in`：`WeatherService` 会把 `result.Source` 盖成胜出数据源的 `provider.Name`，
断言要按大小写不敏感匹配。

**踩过的坑：`dotnet publish` 的增量拷贝会跳过"比源文件新"的目标文件。**
手动改过发布目录的 `appsettings.json` 后，再 publish 时 MSBuild 认为目标更新而跳过拷贝，
发布目录里留着手改的旧配置——表现为"改了源码却没生效"。排查配置类问题时要意识到这一层。

### 按功能分配数据源验证（pywinauto + preferences.json）

`tools/verify_feature_source.py` 自包含验证「按功能分配数据源」：把
`{"Current": "OpenMeteo", "Forecast": "Wttr"}` 合并进
`%LOCALAPPDATA%\SkyView\preferences.json`（保留单位等其它偏好，跑完自动还原），
起应用、拉高窗口、点刷新，用 UIA 断言三处来源标注各自走了指派的源：

```bash
python tools/verify_feature_source.py
```

实测（2026-10-29）**6/6 通过**：`来源：Wttr`（温度趋势卡）+ `来源： OpenMeteo`（当前天气）
+ `来源：OpenMeteo`（逐小时，未指派走自动链）同时出现在同一界面——
即「天气预报用 wttr.in、当前天气用 Open-Meteo」的 per-feature 分流在真机生效。

**踩过的坑：窗口不够高时，UIA 读不到折在可视区外的区块。** 首跑 5/6，
`ForecastSourceText` 报元素不存在；截图显示趋势卡只剩标题、来源标注和图表都在窗口下沿外。
根因是 WinUI 的 UIA 树不含 ScrollViewer 视口外元素（详见第十节 pywinauto 坑 #5），
不是绑定或数据问题。把窗口拉高到 1000px 后一次通过。

### 数据源独立成页 + 实时保存验证（pywinauto + preferences.json）

`tools/verify_settings_live.py` 自包含回归「设置改完不点任何保存，主页立刻跟着变」。
改造内容（2026-10-29）：

- **数据源从设置页搬到独立「数据源」页**（导航第三项，夹在「城市」与「设置」之间）：
  Provider 列表（Key / 优先级 / 测试连接）与「功能数据源分配」卡片整体迁移；
  设置页只剩「显示与刷新」（单位 / 自动刷新间隔 / 逐小时条数 / 生活指数开关）+ 日志目录 + 版本号。
- **保存按钮整体移除，改实时保存**：`SettingsViewModel` 自订阅 `PropertyChanged` →
  `MarkDirty` → `ScheduleSave`（防抖 400ms，`async void` + `ConfigureAwait(true)` 保证落盘在 UI 线程）→
  `Persist()` → `Saved` 事件携带 `(AppPreferences, bool dataSourcesChanged)`，
  由 `App.xaml.cs` 接线到 `WeatherViewModel.ApplyPreferences` + 按需刷新。
  两个防反馈环卫兵：`_suppressSave`（构造/加载期不保存）、`_persisting`（Persist 回写三个属性不触发二次保存）。
- **数据源类 / 显示类变更分流**：Provider 的 Enabled/Priority/ApiKeyInput/ApiHost 与四个
  FeatureSource 属性 → `dataSourcesChanged=true`（触发天气刷新）；UseFahrenheit/AutoRefreshMinutes/
  HourlyCount/ShowLifeIndex → false（只 ApplyPreferences 不联网）。
- `SettingsViewModel` 是 DI 单例，设置页与数据源页共享同一实例，两处状态天然同步。

实测（2026-10-29）**10/10 通过**：设置页翻 `UnitToggle`（**不点任何保存**）→ 轮询等
`preferences.json` 的 `UseFahrenheit` 翻转 → 数据源页把「预报」功能数据源从 Open-Meteo 切到
wttr.in → 回主页**不点刷新**，轮询等单位整体翻转 + `ForecastSourceText` 换成 Wttr；
断言两页均无「保存设置」按钮、截图 `docs/shots/11-保存即生效.png`、进程无崩溃。
（上海 75.2°F → 24°C，21 条温度文本整体翻转。）

改造前修的 bug（根因仍值得记住，详见上节「缓存键含数据源签名」）：

1. **温度单位切换只翻了一半。** 主页大温度绑的是 `Current.Temperature` + 静态转换器
   `TempConverter`（转换器内部读静态 `UnitSettings.UseFahrenheit`），而单位切换只
   `Notify` 了 `TemperatureText`——绑原始值的 TextBlock 永远等不到重新求值。
   体感温度/逐小时/趋势区间都正常，唯独大字不变，极具误导性。
   修法：改绑 `ViewModel.TemperatureText`（与转换器输出格式完全一致，
   `TemperatureConverter.Convert` 内部就是 `WeatherFormat.Temperature`）。
   **教训：`{x:Bind}` + 读静态状态的转换器，静态状态变了不会重求值**，
   要么绑最终文本，要么让转换器参与变更通知。
2. **来源标注不随选择器切换。** 表层是三个来源标注都要等下一次取数才更新；
   **深层根因是缓存键不含数据源信息**——`WeatherService` 的缓存键原是
   `forecast:{city}:{span}`（TTL 10 分钟），切换指派源后刷新直接命中旧源缓存，
   来源标注 10 分钟内不换。修法：缓存键纳入「启用链签名 + 指派源」
   （`ResolveFeature` 返回的 `string.Join(">", 提供器名)` + `#指派源`，四个键全部带上），
   外加保存后自动刷新。组件测试补了 2 个回归用例钉住这个行为。
   **教训：任何带 TTL 的缓存，键里必须包含会影响取数结果决策的全部输入。**

三个顺带修的问题：

- **免 Key 源被误报「已启用却没有 API Key」**：`ProviderConfig.RequiresApiKey`
  原是配置字段，旧版 `appsettings.json` / `settings.dat` 里没有它，反序列化只能拿
  默认值 `true`。改为**按名字推导**（`IsKeyless`：Open-Meteo / wttr.in 免 Key），
  旧配置无需迁移；保存告警、`TestConnection` 门禁、`ApiKeyHint`（「免 Key 数据源，无需填写」）同步受益。
- **ctypes 的 `Structure` 可以静默设置未定义属性**：截图脚本的 `BITMAPINFOHEADER`
  漏了 `biPlanes` 字段，`hdr.biPlanes = 1` 不报错但压根不进 C 布局 →
  `GetDIBits` 返回 0、截图失败。ctypes 不校验属性名，拼结构体时漏一个字段
  不会报错，只会静默错位——很难 debug。
- **Windows 上 `Popen.terminate()` 是硬杀**（等价 `TerminateProcess`，无优雅关闭），
  验证脚本清理进程时如果希望应用自己落盘配置，得先发关闭消息再等退出。

### 本轮（数据源页 + 实时保存）新踩的坑

- **`AutomationProperties.AutomationId` 挂在没有 AutomationPeer 的控件上等于没挂。**
  「功能数据源分配」卡片原来把 AutomationId 挂在外层 `Border` 上——WinUI 的 `Border`
  不是控件，UIA 树里根本不产生 peer，pywinauto 按 `auto_id` 永远找不到，
  报「卡片缺失」的假故障。修法：锚点改挂卡片标题 `TextBlock`（有 peer）。
  **规律：UI 自动化锚点一律挂 TextBlock / 真实控件，别挂 Border / Panel 这类布局元素。**
- **pywinauto 的 `child_window(auto_id=...)` 是延迟解析的。** 它返回
  `WindowSpecification` 而不查 UIA 树，`spec is None` 永远为 False——
  验证脚本里「断言某按钮已移除」因此恒失败。必须调 `spec.exists()` 真正查一次树。
  这类" spec 对象不是元素"的陷阱对所有 `child_window` 返回值的真值判断都成立。
- **`dotnet publish` 增量拷贝会跳过"比源文件新"的目标文件**：手改发布目录后
  再次 publish 不刷新它们，UI 脚本会静默跑在旧构建上。保险做法：
  验证前先删发布目录再 publish。
- **发布目录可能被 CI 下载的旧产物占据**：本机 `publish/` 一度还是 v0.2.0 的
  CI 附件解包内容（版本信息 `0.2.0+2c8be16`），跑 UI 脚本全数报旧结构。
  判断构建新旧不能只看时间戳（MSBuild 拷贝保留源 mtime），要看程序集版本
  或直接 grep dll 里的新类型名——**注意中文字符串在 dll 元数据里是 UTF-16LE，
  `grep` 按 UTF-8 匹配会假报"不存在"**。

### 本轮（tier-2：15 天预报 + 分钟级降雨）新踩的坑

- **验证脚本的前提可能被本机遗留偏好悄悄拆掉**：`preferences.json` 里
  `FeatureSources.Current=Wttr` 是早轮 verify 脚本的残留，而 tier-2 两项能力
  （15 天 / 分钟级）都是 Open-Meteo 专属——不断言"当前源是谁"就直接断言
  "UV 徽标有值"类前提会时灵时不灵。修法：`verify_realdata.py` 启动前把
  Current/Forecast 两个功能临时强制指向 Open-Meteo（备份/还原 preferences.json），
  断言前提自含。**规律：真跑验证脚本要把"数据来自哪个源"也纳入自包含控制。**
- **来源标注用的是 `provider.Name` 不是 `DisplayName`**：`WeatherService` 刻意用
  胜出源的 `Name` 覆盖 `result.Source`（wttr.in 显示成 "Wttr" 同理），
  所以 UI 上是「来源：OpenMeteo」而数据源页里叫「Open-Meteo」。
  断言字符串写 "Open-Meteo" 会假失败一次。
- **Open-Meteo daily 的 `sunrise`/`sunset` 是地点本地时区、无偏移后缀**
  （`"2026-10-29T06:12"`）：不能 `DateTimeOffset.TryParse` 后再 `ToLocalTime()`——
  一旦按本地时区转换会整体偏移。直接按 `T` 切片取 HH:mm。
- **minutely_15 的 stub 时间必须动态生成**（从"当前 15 分钟整点槽"起排）：
  写死时间会被"从当前时刻起"的过滤逻辑整体丢掉——hourly 测试踩过同一个坑，
  tier-2 在分钟级上又踩了一遍边缘。
- **「无数据」与「无雨」是两种状态，不能合并**：minutely_15 缺字段/空数组返回
  null（界面整行隐藏）；有数据但窗口内无雨返回结论（界面显示"未来 2 小时无雨"）。
  用"0 表示未知"会把后者误判成前者。
- **Demo 模式截图不会自动长出新功能**：`DemoProvider` 不补演示值，
  文档截图里新功能就"不存在"——本轮截图复查才发现「短临降雨」行没演出来。
  新增展示字段时记得同步 DemoProvider（它同时是 UI 自动化测试的稳定数据源）。
- **趋势图 15 个点时底部日期标签会叠字**：640px 画布 14 个间隔 ≈ 40px/点，
  "MM-dd" 约 34px 已经贴住。按 `ceil(count/8)` 步长抽稀、最后一天始终保留。

### 本轮（tier-3：花粉 + 恶劣天气预警 + 导航收窄）新踩的坑

- **`AutomationId` 挂在 `ItemsControl` 上等于没挂（同一个坑第三次踩）**：预警卡是
  `ItemsControl` + `DataTemplate`，最初把 `AutomationId="AlertsList"` 挂在外层容器上——
  `ItemsControl` 不产生 AutomationPeer，UIA 树里压根看不到，pywinauto 数不出条数。
  修法：锚点移到 `DataTemplate` 内的 `TextBlock`（`AlertKind` / `AlertLevel`），
  用 `descendants()` 扫描计数。**规律：UI 自动化锚点一律挂有 peer 的真实控件，
  Border / Panel / ItemsControl / ScrollViewer 这类一律不挂**（FeatureSourcesCard、
  AlertsList 两次栽在同一处，事不过三）。
- **Open-Meteo 花粉 API 实测只覆盖欧洲**：`air-quality-api.open-meteo.com` 的 5 个
  花粉变量（grass/grass_pollen + birch/mugwort/alder/olive_pollen）对慕尼黑返回
  0.0 值、对北京/上海五变量全 null。功能仍然实现（欧洲用户可用），国内城市
  界面整行隐藏——**「接口通但数据为 null」与「接口挂了」在 UI 上必须表现一致**，
  都走 `HasData=false` 隐藏，不能显示「花粉：0 粒/m³」误导用户。
- **恶劣天气预警没有免 Key 官方源**：和风有 `weather/now` 附属的 warning 信息但
  Key 失效；Open-Meteo / wttr.in 都没有官方预警字段。方案是从 daily/current 按
  国内常用阈值**本地推导**（与「气压趋势」同款思路，纯函数可单测）：
  降水概率 ≥60 黄 / ≥80 橙、高温 ≥35 橙 / ≥37 红、寒潮 ≤-10、阵风 ≥62 km/h 大风。
  设计决策：同日同类只保最高级（避免「10-31 暴雨」黄橙两条重复）、按日期近→远、
  封顶 5 条（预警卡不是无限列表）；`Reason` 文本含温度/概率数值，
  **切单位必须重推导**（`RebuildAlerts` 在 `ApplyUnit` 里也调一次）。
- **`NavigationView` 默认 `OpenPaneLength=320` 对短导航项太宽**：4 个两字导航项
  （天气/城市/数据源/设置）在 320px 展开栏里左右各一大片空白。收窄到
  `OpenPaneLength="200"` + `CompactPaneLength="48"`（图标轨 48 是 WinUI 3 默认值），
  截图确认比例协调。
- **预警真实链路这次真触发了**：`verify_realdata.py` 对上海真实数据一次推出
  4 条（黄色×2 + 橙色×2，均与降水概率有关）——本地推导不是"永远为空"的摆设；
  但断言要写成"有卡则条数 ≥1 且等级合法，无卡则记『未触发』也算通过"，
  不能硬钉条数（天气每天都变）。

### 双 flavor 发布（framework-dependent / self-contained）

Release workflow（`.github/workflows/release.yml`）与 huanying 同款 matrix，
每次发版产出两个 zip：

| flavor | 参数 | 体积 | 目标机要求 |
|--------|------|------|-----------|
| framework-dependent | `--self-contained false` + `WindowsAppSDKSelfContained=true` | ~139 MB | 需装 .NET 10 Desktop Runtime |
| self-contained | `--self-contained true` | ~216 MB | 解压即用 |

本地干跑两种 flavor 的**关键发现：framework-dependent 包在本机起不来**，
报错对话框标题恰好是 "SkyView"——会骗过 `app.window(title="SkyView")` 的等待逻辑，
表现为"窗口起来了但没内容"。根因：本机 dotnet 装在 `C:\dotnet`（非默认路径、
未写注册表），apphost 找不到运行时。设 `DOTNET_ROOT=C:\dotnet` 后 f-d 包完整运行
（温度、三处来源标注全部正常）——**flavor 本身是对的，纯属本机环境特例**。
在 CI 的 windows 镜像上两者都自带完整工具链，不存在这个问题。

另注意：**v0.1.0 那次 Release 跑在旧单包 workflow 上**（附件只有
`SkyView-v0.1.0-win-x64.zip`），双 flavor matrix 是之后的提交 `0cdf475` 加的，
从未在 CI 跑过。下次发版即是它的首次实战。

**WinUI 单实例互斥的坑**：复现脚本起不来时，先查是不是用户的旧实例还在跑
（命名互斥量 `Global\SkyView.SingleInstance`，第二个实例会激活旧窗口后自己退出），
窗口还可能已收进托盘——`EnumWindows` 看不到，但进程还在。

## 十一、多数据源使用说明

1. 启动应用 → 左侧导航「数据源」
2. 为要用的数据源填 API Key（和风天气 / OpenWeatherMap / 心知天气）
3. 勾选「启用」，设优先级（数字越小越优先）
4. **没有「保存」按钮**——改动防抖 400ms 后自动落盘（Key 经 DPAPI 加密写入 `settings.dat`），数据源工厂立即重载，天气按需自动刷新
5. 数据源类改动（Key / 优先级 / 启用 / 按功能分配）会自动触发一次天气刷新；显示类改动（单位 / 刷新间隔 / 逐小时条数）只即时换算不联网
6. 失败会按优先级自动降级到下一个源

### 各数据源备注

| 数据源 | 说明 |
|--------|------|
| 和风天气 QWeather | 先用 GeoAPI 把城市名解析成 LocationID，再查实时/预报；需要账号专属 API Host |
| OpenWeatherMap | 实时用 `/weather`，预报用 `/forecast`（3 小时粒度，按天聚合取最高/最低） |
| 心知天气 Seniverse | 实时 `/weather/now.json`，预报 `/weather/daily.json`，参数以字符串返回需容错解析 |
| **Open-Meteo** | **免 Key 兜底源**：城市名经内置免密钥地理编码换成经纬度，再查 `/v1/forecast`；WMO `weather_code` 归一成中文短词。默认启用、优先级最低（5），排在降级链最后，确保"所有付费源都挂了也还有真实天气"。tier-1 指标全部可得：current 的 `uv_index` / `precipitation` / `cloud_cover` / `dew_point_2m` / `wind_gusts_10m` + hourly 的 `pressure_msl`。tier-2 也全部可得：daily 支持 `forecast_days=15`（含 `precipitation_probability_max` / `sunrise` / `sunset`，时间串为地点本地时区、无偏移后缀，取 HH:mm 不能整体 Parse 转换），`minutely_15` 15 分钟粒度降水覆盖整个预报窗口（本地截「未来 2 小时」算短临降雨）。tier-3 花粉部分可得：Air Quality API（`air-quality-api.open-meteo.com`）有 5 个花粉变量（草/桦/豚草/桤/橄榄），树类取四变量最大值；**实测只覆盖欧洲**（慕尼黑返回 0.0 值），国内城市五变量全 null，界面整行隐藏。恶劣天气预警**没有免 Key 官方源**（和风有 alerts 但 Key 失效），由 `WeatherAlertEngine` 从 daily/current 按国内阈值本地推导（见覆盖清单），与数据源无关 |
| **wttr.in** | **免 Key 独立兜底源**（参考 [Breezy Weather](https://github.com/breezy-weather/breezy-weather) 的免密钥源思路补充）：URL 直接接受城市名（`/北京?format=j1`），**不依赖 GeocodingService**，与 Open-Meteo 是两条互不依赖的免密钥链路。`format=j1` 返回 `current_condition[0]` + `weather[]`（3 天 × 8 条 3 小时粒度 hourly）；**所有数值都是字符串**（`"temp_C":"18"`），天气描述为英文（经 `MapCondition` 归一），风向是 16 方位（`WWN`→西北偏西风），`time` 形如 `"0"/"300"/"1200"`（小时×100）。默认启用、优先级 6（Open-Meteo 之后的最后一道保底）。tier-1 指标字段位置分散：current 有 `uvIndex`/`cloudcover`/`precipMM`，**露点/阵风只在 hourly**（`DewPointC`/`WindGustKmph`），实现取"当天最接近当前时刻"的那条 hourly。tier-2 无对应能力：预报最多 3 天（`GetForecastAsync` 按 `Math.Clamp(days,1,3)` 截断）、没有分钟级降雨（`MinutelyRain` 为 null，界面整行隐藏）；tier-3 也没有花粉数据（`Pollen` 为 null，界面整行隐藏），预警本地推导照常生效（只依赖 daily/current 通用字段） |

### 付费 Key 实测状态（2026-10-29，按 breezy-weather 用法复测）

用户先后两次提供的 OWM / AccuWeather / PirateWeather 三个 Key 经 DPAPI 解密比对为**同一批**，
按 breezy-weather 源码的调用方式逐一实测均失败：**OWM 401 Invalid API key**、
**Accu 403 "This API Key has expired"**（响应是 gzip，curl 要加 `--compressed` 才看得见错误体）、
**PirateWeather 401 Unauthorized**。换新 Key 前这三个源不会贡献数据，应用自动降级到
Open-Meteo → wttr.in（均已实测可用），功能无感。各源 Key 的重新签发入口：
openweathermap.org/api_keys、developer.accuweather.com、pirateweather.net。

breezy-weather 对这三个源的用法（源码实证，SkyView 实现与之一致）：
Accu 区分 developer(`dataservice.accuweather.com`)/enterprise(`api.accuweather.com`) 双 portal，
`apikey` 走 query，流程是先 translate/geoposition 换 locationKey 再查
currentconditions/forecasts/alerts，developer 档 hourly 上限 120 小时；
OWM 的 `appid` 走 query、坐标入参；PirateWeather 的 **Key 拼在 URL 路径里**
（`/forecast/{key}/{lat},{lon}`），Dark Sky 兼容响应，`units=si/us` + `extend=hourly`。

### 按功能分配数据源（per-feature source）

参考 Breezy Weather 的 per-feature 模型：除全局优先级降级链外，四类功能可分别指定数据源。

1. 数据源页 →「功能数据源分配」卡片，四个下拉框分别对应 **当前天气 / 天气预报 / 逐小时 / 生活指数**，选「自动」即回落到全局优先级链
2. 指派的源失败时不会中断：该功能继续沿优先级链降级，只是不再固定用它
3. 指派的源被取消启用时，下拉框选项即时重建，该功能作废回「自动」
4. 主页三个区块分别标注实际来源（当前天气 / 温度趋势 / 逐小时），不同区块可以不同源

实现要点：
- `WeatherService.OrderForFeature(feature)` 每次取数前把指派的源排到降级链首，其余源保持原优先级垫后
- `AppPreferences.FeatureSources`（`Dictionary<string, string>`，键为功能 ID）持久化在 `preferences.json`，不经过 DPAPI
- 组件测试 `tests/SkyView.ComponentTests/FeatureSourceSelectionTests.cs`（6 例）覆盖指派生效、回落、等同自动、持久化往返

### 城市页瀑布流卡片

「城市」页用 `GridView`（默认 `ItemsWrapGrid`）做自适应卡片网格：每张卡固定宽 220，窗口变宽时一行容纳更多卡、自动换行，即"瀑布式卡片"。
后台对每张卡并发调用 `GetCurrentWithFallbackAsync` 取当天天气，结果通过 `DispatcherQueue.TryEnqueue` 回填到卡片 ViewModel。

**踩过的坑：`{x:Bind}` 默认 `Mode=OneTime`，异步回填不刷新。**
卡片数据是先建卡、再后台取数回填的。`x:Bind` 默认是 `OneTime`，项首次被 `GridView` 虚拟化实现时绑定到初始值（如 `IsLoading=true`），
之后 `IsLoading→false` / `Current` / `DisplayTemperature` / `HasData` 的变化**不会**更新 UI——表现为"卡片永远转圈、偶尔（容器在被虚拟化前数据已到）又正常"的诡异非确定性现象。
修法：在 Page 根加 `x:DefaultBindMode="OneWay"`，让卡片的属性绑定全部变 OneWay。调试时可在 `LoadCardAsync` 写文件日志确认"数据其实已经拿到、只是没渲染"。

## 十二、天气图标库与动画库

### 分层

| 位置 | 职责 |
|------|------|
| `SkyView.Core/Icons/WeatherIconKind.cs` | 12 种图标的语义枚举（晴/多云/阴/小雨/雨/大雨/雷阵雨/雪/雾霾/沙尘/大风/未知） |
| `SkyView.Core/Icons/WeatherIconSelector.cs` | 天气文字 → 枚举，有序规则表，可单元测试 |
| `SkyView/Controls/WeatherIcon.xaml(.cs)` | 矢量绘制 + 环境动效，`Kind` / `IsAnimated` 依赖属性 |
| `SkyView/Motion/MotionLibrary.cs` | 入场淡入上浮、图标弹出，尊重系统「减少动画」 |

分类逻辑放在 Core 而不是控件里，是为了能脱离 UI 测试：`WeatherIconSelectorTests`
有 44 个用例覆盖中文/英文/强度词/优先级/兜底。

### 为什么不用 Path 字符串画图标

`Path.Data` 是**运行时**解析的，写错一个数字编译期发现不了，加载时抛异常 →
`InitializeComponent()` 失败 → 进程秒退 `0xC000027B`。这个坑本项目踩过两次。

所以图标全部用 `Ellipse` / `Rectangle` / `Line` / `Polygon` 拼，坐标是纯数字，
零解析风险，而且每个图元都能单独挂动画。

### 图层组合

一套 24×24 画布，图层顺序即 z 序：
太阳 → 背景云 → 主云 → 降水（5 个雨滴 + 3 片雪花）→ 闪电 → 雾线 → 风线。

| 图标 | 使用的图层 |
|------|------------|
| 晴 | 太阳（光芒缓慢旋转） |
| 多云 | 太阳缩到右上 + 云缩到左下 |
| 阴 | 背景云缩 0.82 右上错开 + 主云 |
| 小雨 / 雨 | 主云 + 2 / 3 颗雨滴（对称取位） |
| 大雨 | 主云刷成板岩灰 + 5 颗雨滴 |
| 雷阵雨 | 同上 + 闪电（明暗闪烁） |
| 雪 | 主云 + 3 片雪花（慢速飘落 + 横向摆动） |
| 雾霾 | 4 条灰雾线（各自左右浮动） |
| 沙尘 | 同上，雾线刷成土黄 |
| 大风 | 3 条风线 + 右侧小卷曲 |
| 未知 | 一朵中性云 |

几个设计上的坑，都是截图看出来的：

- **云不能只用几片等大椭圆**，看起来像个荷包蛋。要「左小鼓包 + 右大鼓包 + 圆角平底」，
  三个图元底边对齐在同一条线上，拼出来的剪影才像云。
- **大雨的云不能用纯蓝**（试过 `SteelBlue`），会读成一块蓝色色块。用板岩灰 `#546E7A`，
  再靠雨滴数量和颜色表达强度。
- **雪花不能用很浅的淡蓝**（`#B3E5FC`），白底卡片上基本看不见。改用 `#64B5F6` 并把直径
  从 2.2 放大到 3.4。
- **雨滴等距排布**（6.4 / 9.6 / 12.8 / 16.0 / 19.2，中心 12.8），取「偶数位」或「首中尾」
  都是对称的；直接取前 N 个会一边重一边轻。

### 强度信号从哪来

上游 `MapCondition` 会把「小雨 / 中雨 / 大雨」一律压成「雨」，文字里的强度信息就没了。
`WeatherIconSelector.Select(condition, precipitationProbability)` 用降水概率补一刀：
普通的雨 + 概率 ≥ 70% → 大雨图标。至少不会出现「暴雨却挂小雨图标」。

### 动画

图标动效是 `UserControl.Resources` 里的 Storyboard，全部 `RepeatBehavior="Forever"`，
幅度都很小（位移 < 1px、太阳 24 秒一圈），目的是「活的」而不是「在跳舞」。

入场动效用 `MotionLibrary.PlayEntrance` / `PlayPopIn`（Storyboard + 组合变换），
卡片按 190ms 起步、每张递增 70ms 错峰出现。**只在数据首次出现时播一次**，
自动刷新不重播 —— 每 30 分钟整页弹一下很烦人。

两处必须注意：

- **列表里的图标 `IsAnimated="False"`**。一屏二十几个图标同时跑 Storyboard 不只是耗电，
  视觉上也是一锅粥。未使用的图层 `Collapsed`，不再参与布局。
- **尊重系统的「减少动画」**（`UISettings.AnimationsEnabled`）。关掉的用户不少是前庭功能敏感，
  硬播会让人不适。`MotionLibrary` 会直接跳过并把元素设为不透明，不是简单地把动画删掉。

### 无障碍

`WeatherIcon` 重写了 `OnCreateAutomationPeer()`。UserControl 默认没有 AutomationPeer，
不加这个在 UIA 树里根本看不到——屏幕阅读器读不到，自动化也断言不到。
现在它在树里以「天气图标：晴」的名字出现，pywinauto 能直接校验分类是否正确。

## 十三、第四梯队：桌面化体验增强

> 这一批把应用从「能看」做成「能常驻、好定位、自带身份」。

### 系统托盘（关闭到托盘 + 悬停显示温度）

WinUI 3 至今没有官方 `NotifyIcon`。两个坑：

- **不能借 WinForms 的 `NotifyIcon`**。第一反应是用 `UseWindowsForms=true`，
  但 WinForms 的 XAML 编译目标会和 WinUI 的冲突，直接报 `MC6000`
  （`MarkupCompilePass1` / `GenerateTemporaryTargetAssembly` 撞车）。**这条路走不通**。
- 改为直接 P/Invoke `Shell_NotifyIcon`（`NOTIFYICONDATA` + `NIF_ICON|NIF_MESSAGE|NIF_TIP`），
  自建一个隐藏消息窗口收 `WM_TRAYMSG`。托盘 tooltip 显示「城市 温度」，
  右键菜单含「显示主窗口 / 刷新 / 退出」。关闭窗口时不退出进程，而是 `Hide` + 留托盘。

### 单实例运行

命名互斥量 `Global\SkyView.SingleInstance`。重复启动时拿不到锁，就发一条自定义
窗口消息（`WM_SHOWME`）给已有窗口，由它 `AppWindow.Show()` 并置前，自己直接退出。
**必须有 `Global\` 前缀**——会话隔离下本地互斥量拦不住第二个登录会话的副本。

### 定位当前城市

`LocationService`：优先 `Windows.Devices.Geolocation.Geolocator`（会弹系统权限申请），
失败时回退到 IP 定位（`ip-api.com`，免密钥）。拿到经纬度后反查城市名并切到该城市。
**权限被拒要给出明确提示**而不是静默失败——实测无定位权限时直接报
「定位权限被拒绝。请到『设置 → 隐私和安全性 → 位置』中开启」。

### 新数据源：AccuWeather / PirateWeather（OpenWeatherMap 原有）

这俩都要求**经纬度**入参（AccuWeather 还要先查 `locationKey`）。
为此新增 `GeocodingService`：基于 [Open-Meteo](https://open-meteo.com/) 的**免密钥**
地理编码，把城市名解析成经纬度——不必为定位单独申请 Key。
`ProviderCatalog` 现含 7 个源：和风天气 / OpenWeatherMap / 心知天气 / AccuWeather / PirateWeather / Open-Meteo / wttr.in，
按 `Priority` 自动降级。`appsettings.json` 与设置页列表同步更新。
Open-Meteo 免 Key、默认启用、优先级 5，作为兜底源；wttr.in 免 Key、优先级 6，最后一道保底。

### 新数据源：wttr.in（参考 Breezy Weather）

调研 [breezy-weather](https://github.com/breezy-weather/breezy-weather)（Kotlin/Android，50+ 数据源）后补上的第二个免密钥通用源。
该仓库的源按 free/paid、按国家划分：Bright Sky 仅覆盖德国（DWD）、NWS 仅覆盖美国且 token 要付费，均不适合；
Open-Meteo 已在用；**wttr.in** 是剩余里最合适的：免 Key、全球通用、URL 直接接受城市名。
与 Open-Meteo 的关键区别是**不依赖 GeocodingService**（自己做地理编码），构成两条独立的免密钥兜底链路。

数据形状上的坑（都在 `WttrProviderTests` 用 stub 固定响应覆盖）：
- **数值全是字符串**：`"temp_C":"18"`，不能直接当 number 读（基类 `GetDouble` 已兼容字符串）；
- 天气描述是英文（`"Sunny"`/`"Moderate rain"`），复用基类 `MapCondition` 归一成中文；
- 风向是 16 方位缩写（`"WWN"`），自建映射表转中文；
- `hourly[].time` 形如 `"0"/"300"/"1200"`（小时×100，也可能 `"000"`），需按两种量纲解析；
- 日出日落是 `"06:39 AM"` 英文格式——为此把基类 `FormatClock` 改成 **InvariantCulture 解析**：
  API 返回值本身是文化无关的，解析就不该依赖机器 locale（原实现跟着 `CurrentCulture` 走，
  在某些区域设置下 "AM/PM" 的识别行为没保证），固定 InvariantCulture 后任何机器结果一致。
- 只有 3 天预报，`days` 参数要 Clamp 到 [1,3]。

**踩过的坑：`Assert.Contains(collection, predicate)` 重载解析。** 断言 URL 包含子串时写成
`Assert.Contains(str, u => u.Contains(...))`，`string` 作为 `IEnumerable<char>` 被匹配到谓词重载，
编译报 CS0037/CS1929。子串断言应写 `Assert.Contains("子串", 字符串)`。
另：记录请求 URL 要取 `RequestUri.AbsoluteUri`——`ToString()` 会把 `%20` 反转义回裸空格，编码断言会失真。

### 天气主题背景

`WeatherTheme`（Core 层，纯配色不依赖 UI）把 12 种图标归成 9 组渐变（小雨/中雨/大雨共用一组，
否则逐小时相邻格背景会跳色）。主页 `Grid` 背景换成 `LinearGradientBrush`，
`OnNavigatedTo` 时按当前天气切色并做过渡动画，尊重系统「减少动画」。

### 版本号取 git tag

`SkyView.csproj` 里 `ResolveVersionFromGitTag` 目标在 `InitializeSourceControlInformation`
之后跑 `git describe --tags`，写入 `Version` / `FileVersion` / `InformationalVersion`，
无 git 时回退 `0.0.0`。`AppInfo` 读 `InformationalVersion` 并**剥掉 SourceLink 的 `+sha` 后缀**
（否则显示成 `1.0.0+abc123`）。设置页展示「版本 v1.0.0」。

### 应用 Logo（WinUI 3 风格）

`tools/make_icon.py` 用 Pillow 画一个 1024² 的 Fluent 风图标：圆角磁贴底 + 太阳（八道圆头光芒）
+ 云（圆角长条 + 两鼓包，圆心压在长条顶边上避免「荷包蛋」轮廓），导出 1024 png、
多尺寸 `.ico`（16–256）和 Store 横幅。接入 `csproj` `ApplicationIcon`、窗口 `AppWindow.SetIcon`、
托盘图标。重画提醒：**两个云鼓包之间要留足间距**，否则会有过深 V 形夹缝。

## 十四、已知警告

- `MVVMTK0045`：`[ObservableProperty]` 字段在 WinRT/AOT 场景的兼容性提示，不影响功能；
  彻底消除需改为 `partial` 属性写法。
