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

        // 功能数据源分配：没指派的功能保持空串 = 「自动」
        CurrentSource = prefs.SourceFor(WeatherFeature.Current) ?? string.Empty;
        ForecastSource = prefs.SourceFor(WeatherFeature.Forecast) ?? string.Empty;
        HourlySource = prefs.SourceFor(WeatherFeature.Hourly) ?? string.Empty;
        LifeIndexSource = prefs.SourceFor(WeatherFeature.LifeIndex) ?? string.Empty;

        TestCity = _config?.GetValue<string>("App:DefaultCity") is { Length: > 0 } city ? city : "北京";

        Providers = [];
        FeatureSourceOptions = [];
        StatusMessage = string.Empty;
        LoadProviders();
    }

    [ObservableProperty]
    public partial ObservableCollection<ProviderConfigItem> Providers { get; set; }

    /// <summary>功能数据源下拉框的选项：「自动」+ 启用中的数据源。随启用状态实时重建。</summary>
    [ObservableProperty]
    public partial ObservableCollection<FeatureSourceOption> FeatureSourceOptions { get; set; }

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

    // ---------- 功能数据源分配（空串 = 自动） ----------

    [ObservableProperty]
    public partial string CurrentSource { get; set; }

    [ObservableProperty]
    public partial string ForecastSource { get; set; }

    [ObservableProperty]
    public partial string HourlySource { get; set; }

    [ObservableProperty]
    public partial string LifeIndexSource { get; set; }

    /// <summary>把界面上的偏好收拢成一个对象。</summary>
    public AppPreferences BuildPreferences() => new()
    {
        UseFahrenheit = UseFahrenheit,
        AutoRefreshMinutes = (int)Math.Round(AutoRefreshMinutes),
        HourlyCount = (int)Math.Round(HourlyCount),
        ShowLifeIndex = ShowLifeIndex,
        FeatureSources = BuildFeatureSources(),
    };

    /// <summary>把四个下拉框的选择收成功能→源字典；「自动」（空串）不写入。</summary>
    private Dictionary<string, string> BuildFeatureSources()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        SetIfAssigned(WeatherFeature.Current, CurrentSource);
        SetIfAssigned(WeatherFeature.Forecast, ForecastSource);
        SetIfAssigned(WeatherFeature.Hourly, HourlySource);
        SetIfAssigned(WeatherFeature.LifeIndex, LifeIndexSource);
        return map;

        void SetIfAssigned(string feature, string? source)
        {
            if (!string.IsNullOrWhiteSpace(source))
                map[feature] = source.Trim();
        }
    }

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

            var item = new ProviderConfigItem
            {
                Name = d.Name,
                DisplayName = string.IsNullOrWhiteSpace(d.DisplayName) ? d.Name : d.DisplayName,
                ApiHost = string.IsNullOrWhiteSpace(effective.ApiHost) ? d.ApiHost : effective.ApiHost,
                SavedApiKey = effective.ApiKey,
                Enabled = effective.Enabled,
                Priority = effective.Priority,
                RequiresApiKey = d.RequiresApiKey,
                ApiKeyInput = string.Empty,
            };

            // 勾选/取消启用时，功能分配下拉框要跟着重建（选项只含启用中的源）
            item.PropertyChanged += OnProviderItemPropertyChanged;

            Providers.Add(item);
        }

        RebuildFeatureSourceOptions();
    }

    /// <summary>启用状态变化 → 重建「功能数据源分配」的选项。</summary>
    private void OnProviderItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProviderConfigItem.Enabled))
            RebuildFeatureSourceOptions();
    }

    /// <summary>
    /// 重建功能分配下拉框：「自动」+ 当前启用中的数据源。
    /// 已指派但被取消启用的源，其指派就地作废（回到「自动」）——
    /// 运行时本来也会因源不在启用列表而回退到自动链，这里让界面说实话。
    /// </summary>
    private void RebuildFeatureSourceOptions()
    {
        FeatureSourceOptions.Clear();
        FeatureSourceOptions.Add(new FeatureSourceOption { Name = string.Empty, DisplayName = "自动（按优先级）" });

        foreach (var p in Providers.Where(p => p.Enabled))
            FeatureSourceOptions.Add(new FeatureSourceOption { Name = p.Name, DisplayName = p.DisplayName });

        ResetIfDisabled(WeatherFeature.Current, () => CurrentSource, v => CurrentSource = v);
        ResetIfDisabled(WeatherFeature.Forecast, () => ForecastSource, v => ForecastSource = v);
        ResetIfDisabled(WeatherFeature.Hourly, () => HourlySource, v => HourlySource = v);
        ResetIfDisabled(WeatherFeature.LifeIndex, () => LifeIndexSource, v => LifeIndexSource = v);

        void ResetIfDisabled(string feature, Func<string> get, Action<string> set)
        {
            var assigned = get();
            if (string.IsNullOrEmpty(assigned))
                return;
            if (FeatureSourceOptions.Any(o => string.Equals(o.Name, assigned, StringComparison.OrdinalIgnoreCase)))
                return;
            set(string.Empty);
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

            // 免 Key 数据源（Open-Meteo / wttr.in）不参与"启用了却没填 Key"的告警
            if (p.Enabled && p.RequiresApiKey && string.IsNullOrWhiteSpace(apiKey))
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
        if (item.RequiresApiKey && string.IsNullOrWhiteSpace(config.ApiKey))
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
