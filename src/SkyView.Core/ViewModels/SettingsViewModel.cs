using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using SkyView.Models;
using SkyView.Providers;
using SkyView.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace SkyView.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISecureConfigStore _store;
    private readonly WeatherProviderFactory _factory;
    private readonly IConfiguration? _config;
    private readonly IPreferencesStore? _preferencesStore;

    public SettingsViewModel(
        ISecureConfigStore store,
        WeatherProviderFactory factory,
        IConfiguration? configuration = null,
        IPreferencesStore? preferencesStore = null)
    {
        _store = store;
        _factory = factory;
        _config = configuration;
        _preferencesStore = preferencesStore;

        var prefs = preferencesStore?.Load() ?? new AppPreferences();
        UseFahrenheit = prefs.UseFahrenheit;
        AutoRefreshMinutes = prefs.NormalizedAutoRefreshMinutes;
        HourlyCount = prefs.NormalizedHourlyCount;
        ShowLifeIndex = prefs.ShowLifeIndex;

        TestCity = _config?.GetValue<string>("App:DefaultCity") is { Length: > 0 } city ? city : "北京";

        Providers = [];
        StatusMessage = string.Empty;
        LoadProviders();
    }

    [ObservableProperty]
    public partial ObservableCollection<ProviderConfigItem> Providers { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    public partial string StatusMessage { get; set; }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    /// <summary>「测试连接」用的城市。</summary>
    [ObservableProperty]
    public partial string TestCity { get; set; }

    // ---------- 偏好设置 ----------

    [ObservableProperty]
    public partial bool UseFahrenheit { get; set; }

    /// <summary>自动刷新间隔（分钟）。0 = 关闭。</summary>
    [ObservableProperty]
    public partial double AutoRefreshMinutes { get; set; }

    [ObservableProperty]
    public partial double HourlyCount { get; set; }

    [ObservableProperty]
    public partial bool ShowLifeIndex { get; set; }

    /// <summary>把界面上的偏好收拢成一个对象。</summary>
    public AppPreferences BuildPreferences() => new()
    {
        UseFahrenheit = UseFahrenheit,
        AutoRefreshMinutes = (int)Math.Round(AutoRefreshMinutes),
        HourlyCount = (int)Math.Round(HourlyCount),
        ShowLifeIndex = ShowLifeIndex,
    };

    private void LoadProviders()
    {
        Providers.Clear();

        var defaults = _config?.GetSection("WeatherProviders").Get<List<ProviderConfig>>();
        if (defaults is not { Count: > 0 })
            defaults = ProviderCatalog.CreateDefaults().ToList();

        foreach (var d in defaults.OrderBy(x => x.Priority))
        {
            // 设置页展示的必须是"工厂实际会用到"的那份值，否则会出现
            // appsettings 里 Enabled=false 但界面上显示为勾选的错位。
            var saved = _store.Get<ProviderConfig>($"provider:{d.Name}");
            var effective = saved ?? d;

            Providers.Add(new ProviderConfigItem
            {
                Name = d.Name,
                DisplayName = string.IsNullOrWhiteSpace(d.DisplayName) ? d.Name : d.DisplayName,
                ApiHost = string.IsNullOrWhiteSpace(effective.ApiHost) ? d.ApiHost : effective.ApiHost,
                SavedApiKey = effective.ApiKey,
                Enabled = effective.Enabled,
                Priority = effective.Priority,
                ApiKeyInput = string.Empty,
            });
        }
    }

    [RelayCommand]
    public void Save()
    {
        var warnings = new List<string>();

        foreach (var p in Providers)
        {
            var apiKey = p.ResolveApiKey();

            _store.Set($"provider:{p.Name}", new ProviderConfig
            {
                Name = p.Name,
                DisplayName = p.DisplayName,
                ApiHost = p.ApiHost?.Trim() ?? string.Empty,
                ApiKey = apiKey,
                Enabled = p.Enabled,
                Priority = (int)Math.Round(p.Priority),
            });

            // 保存后同步到界面状态，避免再次保存时误以为"还是旧 Key"
            p.SavedApiKey = apiKey;
            p.ApiKeyInput = string.Empty;

            if (p.Enabled && string.IsNullOrWhiteSpace(apiKey))
                warnings.Add(p.DisplayName);
        }

        _preferencesStore?.Save(BuildPreferences());
        _factory.Reload();

        StatusMessage = warnings.Count > 0
            ? $"已保存（{DateTime.Now:HH:mm:ss}）。但 {string.Join("、", warnings)} 已启用却没有 API Key，刷新仍会失败。"
            : $"已保存（{DateTime.Now:HH:mm:ss}），已生效。";
    }

    /// <summary>
    /// 测试单个数据源的连通性。用界面上**当前输入**（包括还没保存的 Key）去试，
    /// 这样用户填完就能立刻知道对不对，不用先保存再回天气页看错误。
    /// </summary>
    [RelayCommand]
    public async Task TestConnectionAsync(ProviderConfigItem? item)
    {
        if (item is null || item.IsTesting)
            return;

        var config = item.ToConfig();
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            item.TestSucceeded = false;
            item.TestMessage = "✗ 请先填写 API Key 再测试。";
            return;
        }

        item.IsTesting = true;
        item.TestSucceeded = null;
        item.TestMessage = "正在测试…";

        try
        {
            var provider = _factory.CreateProvider(config);
            if (provider is null)
            {
                item.TestSucceeded = false;
                item.TestMessage = $"✗ 未知的数据源：{config.Name}";
                return;
            }

            var city = string.IsNullOrWhiteSpace(TestCity) ? "北京" : TestCity.Trim();
            var result = await provider.TestConnectionAsync(city).ConfigureAwait(true);

            item.TestSucceeded = result.Success;
            item.TestMessage = result.Success
                ? $"✓ {result.Message}（{result.ElapsedMilliseconds} ms）"
                : $"✗ 失败：{result.Message}";
        }
        catch (Exception ex)
        {
            item.TestSucceeded = false;
            item.TestMessage = $"✗ 失败：{ex.Message}";
        }
        finally
        {
            item.IsTesting = false;
        }
    }
}
