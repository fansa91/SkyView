using Microsoft.Extensions.Configuration;
using SkyView.Models;
using SkyView.Providers;
using SkyView.Services;
using SkyView.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace SkyView.ComponentTests;

/// <summary>
/// 实时保存：设置/数据源页的任何改动在防抖间隔后自动落盘，
/// 不需要「保存」按钮。落盘后通过 <see cref="SettingsViewModel.Saved"/> 通知调用方，
/// 并区分「纯显示偏好」与「会影响取数链路的改动」。
/// </summary>
public class SettingsAutoSaveTests
{
    /// <summary>内存版安全存储，供断言 provider 配置是否真的写了进去。</summary>
    private sealed class FakeSecureStore : ISecureConfigStore
    {
        public Dictionary<string, object> Data { get; } = new(StringComparer.OrdinalIgnoreCase);

        public T? Get<T>(string key) where T : class =>
            Data.TryGetValue(key, out var v) ? v as T : null;

        public void Set<T>(string key, T value) where T : class => Data[key] = value;

        public void Remove(string key) => Data.Remove(key);
    }

    private static (SettingsViewModel Vm, FakeSecureStore Store, IPreferencesStore Prefs) CreateVm(
        TimeSpan? debounce = null)
    {
        var store = new FakeSecureStore();
        var factory = new WeatherProviderFactory(new ConfigurationBuilder().Build(), store);
        var prefsPath = Path.Combine(Path.GetTempPath(), $"skyview-autosave-{Guid.NewGuid():N}.json");
        var prefs = new PreferencesStore(prefsPath);

        var vm = new SettingsViewModel(store, factory, null, prefs);
        if (debounce is not null)
            vm.SaveDebounce = debounce.Value;
        return (vm, store, prefs);
    }

    [Fact]
    public async Task DisplayPrefChange_ShouldAutoSave_WithoutRefreshSignal()
    {
        var (vm, _, prefs) = CreateVm(TimeSpan.FromMilliseconds(20));
        var fires = new List<(AppPreferences Prefs, bool DataSources)>();
        vm.Saved += (p, d) => fires.Add((p, d));

        vm.UseFahrenheit = true;
        await vm.SaveTask;

        Assert.True(prefs.Load().UseFahrenheit, "单位切换应已自动落盘");
        var single = Assert.Single(fires);
        Assert.False(single.DataSources, "纯显示偏好不应要求刷新天气");
    }

    [Fact]
    public async Task ApiKeyInput_ShouldAutoSave_WithRefreshSignal()
    {
        var (vm, store, _) = CreateVm(TimeSpan.FromMilliseconds(20));
        var fires = new List<(AppPreferences Prefs, bool DataSources)>();
        vm.Saved += (p, d) => fires.Add((p, d));

        // 和风天气需要 Key：输入即触发落盘，并标记「数据源已变更」
        var qweather = vm.Providers.First(p => p.Name == "QWeather");
        qweather.ApiKeyInput = "  test-key-123  ";

        await vm.SaveTask;

        var saved = store.Get<ProviderConfig>("provider:QWeather");
        Assert.NotNull(saved);
        Assert.Equal("test-key-123", saved!.ApiKey);   // 两端空白被 Trim
        Assert.True(Assert.Single(fires).DataSources);
    }

    [Fact]
    public async Task ToggleEnabled_ShouldRebuildOptionsAndSave()
    {
        var (vm, store, prefs) = CreateVm(TimeSpan.FromMilliseconds(20));

        var openMeteo = vm.Providers.First(p => p.Name == "OpenMeteo");
        Assert.Contains(vm.FeatureSourceOptions, o => o.Name == "OpenMeteo");

        // 把预报指派给 Open-Meteo，再取消启用它 → 指派就地作废回「自动」并落盘
        vm.ForecastSource = "OpenMeteo";
        await vm.SaveTask;

        openMeteo.Enabled = false;
        await vm.SaveTask;

        Assert.DoesNotContain(vm.FeatureSourceOptions, o => o.Name == "OpenMeteo");
        Assert.Equal(string.Empty, vm.ForecastSource);
        Assert.False(store.Get<ProviderConfig>("provider:OpenMeteo")!.Enabled);
        Assert.False(prefs.Load().FeatureSources.ContainsKey("Forecast"),
            "被停用源的指派不应继续留在偏好里");
    }

    [Fact]
    public async Task Persist_ShouldNotFeedbackLoop()
    {
        var (vm, _, _) = CreateVm(TimeSpan.FromMilliseconds(20));
        var fires = new List<int>();
        vm.Saved += (_, _) => fires.Add(0);

        vm.HourlyCount = 18;   // 默认 12，换成不同值才会真的触发属性变更
        await vm.SaveTask;
        // Persist 内部会回写 SavedApiKey / ApiKeyInput / StatusMessage，
        // 这些属性变更不能再触发第二次保存（否则无限循环）
        await Task.Delay(150);

        Assert.Single(fires);
    }

    [Fact]
    public async Task Construction_ShouldNotSave()
    {
        var (vm, _, _) = CreateVm(TimeSpan.FromMilliseconds(20));
        var fires = 0;
        vm.Saved += (_, _) => fires++;

        await Task.Delay(150);   // 超过防抖间隔：加载期若有保存意图早就该触发了

        Assert.Equal(0, fires);
    }

    [Fact]
    public void PersistNow_ShouldFlushImmediately()
    {
        var (vm, store, _) = CreateVm();
        var qweather = vm.Providers.First(p => p.Name == "QWeather");
        qweather.ApiKeyInput = "flush-key";

        vm.PersistNow();

        Assert.Equal("flush-key", store.Get<ProviderConfig>("provider:QWeather")!.ApiKey);
    }

    [Fact]
    public void PersistNow_WithoutChanges_ShouldBeNoOp()
    {
        var (vm, _, _) = CreateVm();
        var fires = 0;
        vm.Saved += (_, _) => fires++;

        vm.PersistNow();   // 构造后没有任何改动

        Assert.Equal(0, fires);
    }
}
