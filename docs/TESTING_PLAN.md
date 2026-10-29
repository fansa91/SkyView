# SkyView 测试计划

本文档定义了 SkyView 天气应用的三层测试体系：单元测试、组件测试、功能测试。

---

## 一、测试分层

| 测试层级 | 项目 | 框架 | 测试范围 | 运行方式 |
|----------|------|------|----------|----------|
| **单元测试** | `SkyView.UnitTests` | xUnit + Moq | ViewModel 逻辑、值转换器、模型验证、Provider 条件映射 | `dotnet test` |
| **组件测试** | `SkyView.ComponentTests` | xUnit + Moq | WeatherService 主备切换、ProviderFactory 配置加载、缓存策略 | `dotnet test` |
| **功能/UI 测试** | `SkyView.UITests` | xUnit + WinAppDriver | 应用启动、主页渲染、设置页 API Key 输入、多源切换、窗口导航 | WinAppDriver + `dotnet test` |

---

## 二、单元测试（SkyView.UnitTests）

### 测试目标
验证纯 C# 逻辑，不依赖 Windows API 和 UI 线程。

### 已覆盖用例
1. `WeatherViewModelTests.RefreshAsync_ShouldSetCurrent_WhenServiceSucceeds`
   - 服务返回成功数据时，ViewModel 属性正确更新；`IsLoading` 结束后为 `false`；`ErrorMessage` 为空。
2. `WeatherViewModelTests.RefreshAsync_ShouldSetError_WhenServiceThrows`
   - 服务抛异常时，ViewModel 捕获并写入 `ErrorMessage`；`IsLoading` 为 `false`。
3. `TemperatureConverterTests.Convert_ShouldFormatTemperature`
   - 温度值格式化为 `22.5°C`。
4. `TemperatureConverterTests.Convert_ShouldHandleNull`
   - 空值不抛异常。

### 如何运行
```bash
cd tests/SkyView.UnitTests
dotnet test
```

---

## 三、组件测试（SkyView.ComponentTests）

### 测试目标
验证跨组件协作：Provider 工厂、WeatherService 聚合、缓存、主备切换（Fallback）。

### 已覆盖用例
1. `WeatherServiceFallbackTests.GetCurrentWithFallbackAsync_ShouldReturnFirstSuccessfulProvider`
   - 第一个 Provider 抛异常，服务自动切换到第二个 Provider 并返回结果。

### 如何运行
```bash
cd tests/SkyView.ComponentTests
dotnet test
```

---

## 四、功能/UI 测试（SkyView.UITests）

### 测试目标
验证完整应用在真实 Windows 环境中的行为。

### 前置条件
1. 下载并安装 [Windows Application Driver (WinAppDriver)](https://github.com/microsoft/WinAppDriver)
2. 启动 WinAppDriver 服务（默认监听 `http://127.0.0.1:4723`）
3. 先编译主应用：`dotnet build src/SkyView`

### 已覆盖用例
1. `AppLaunchTests.Launch_ShouldOpenMainWindow`
   - 启动 SkyView.exe，验证主窗口打开（当前为占位，需 WinAppDriver 运行后替换真实断言）。

### 如何运行
```bash
cd tests/SkyView.UITests
dotnet test
```

---

## 五、持续集成（GitHub Actions）

建议在 `.github/workflows/ci.yml` 中配置：

```yaml
name: CI
on: [push, pull_request]
jobs:
  build-and-test:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - run: dotnet build SkyView.sln
      - run: dotnet test tests/SkyView.UnitTests
      - run: dotnet test tests/SkyView.ComponentTests
```

> UI 测试（WinAppDriver）需要带桌面的运行器，建议单独在自托管 Windows Agent 上运行。

---

## 六、测试数据约定

| 类型 | 约定 |
|------|------|
| 城市名 | 测试中统一使用 `北京` |
| 温度单位 | 摄氏度 (°C)，符合中国习惯 |
| 缓存过期 | 10 分钟 |
| 数据源优先级 | 越小优先级越高（0 最高） |