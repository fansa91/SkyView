using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using SkyView.Formatting;
using SkyView.Models;
using SkyView.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace SkyView.ViewModels;

public partial class WeatherViewModel : ObservableObject
{
    private readonly IWeatherService _weatherService;
    private readonly IWeatherCache? _cache;
    private readonly IPreferencesStore? _preferences;
    private readonly int _forecastDays;
    private AppPreferences _prefs;

    public WeatherViewModel(
        IWeatherService weatherService,
        IConfiguration? configuration = null,
        IWeatherCache? cache = null,
        IPreferencesStore? preferences = null)
    {
        _weatherService = weatherService;
        _cache = cache;
        _preferences = preferences;
        _prefs = preferences?.Load() ?? new AppPreferences();

        UnitSettings.UseFahrenheit = _prefs.UseFahrenheit;
        _useFahrenheit = _prefs.UseFahrenheit;

        var defaultCity = configuration?.GetValue<string>("App:DefaultCity");
        City = string.IsNullOrWhiteSpace(defaultCity) ? "北京" : defaultCity!;

        var days = configuration?.GetValue("App:ForecastDays", 3) ?? 3;
        _forecastDays = days is > 0 and <= 15 ? days : 3;

        Current = new WeatherCurrent();
        ErrorMessage = string.Empty;

        // 离线优先：先把上次成功的数据显示出来，再让界面去后台刷新
        RestoreFromCache();
    }

    [ObservableProperty]
    public partial WeatherCurrent Current { get; set; }

    [ObservableProperty]
    public partial string City { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? LastUpdated { get; set; }

    /// <summary>未来几天预报（数据源不支持或失败时为空集合，不影响当前天气展示）。</summary>
    public ObservableCollection<WeatherForecast> Forecast { get; } = [];

    /// <summary>逐小时预报。</summary>
    public ObservableCollection<WeatherHourly> Hourly { get; } = [];

    /// <summary>生活指数。</summary>
    public ObservableCollection<WeatherLifeIndex> LifeIndex { get; } = [];

    /// <summary>是否已经成功加载过一次天气（首页用于只自动刷新一次）。</summary>
    public bool HasLoaded { get; private set; }

    private bool _hasCurrent;

    /// <summary>
    /// 是否已有真实的当前天气数据。
    /// <para>
    /// <see cref="Current"/> 在构造时是默认实例（温度 0），若直接绑定会把 <c>0°C</c> 当成真实数据展示，
    /// 用户会误以为拿到了天气。这里用一个显式标志控制卡片显隐：只有请求成功过（或从缓存恢复）才显示。
    /// 刷新失败时保留上一次的数据（不清空），避免界面闪成空白。
    /// </para>
    /// </summary>
    public bool HasCurrent
    {
        get => _hasCurrent;
        private set
        {
            if (_hasCurrent == value)
                return;

            _hasCurrent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEmptyState));
        }
    }

    private bool _isFromCache;

    /// <summary>当前显示的是否为本地缓存（离线/首次启动尚未刷新成功）。</summary>
    public bool IsFromCache
    {
        get => _isFromCache;
        private set
        {
            if (_isFromCache == value)
                return;

            _isFromCache = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CacheHint));
        }
    }

    /// <summary>缓存提示文案，如「显示的是 12 分钟前的缓存数据」。</summary>
    public string CacheHint
    {
        get
        {
            if (!_isFromCache || LastUpdated is null)
                return string.Empty;

            var age = DateTimeOffset.Now - LastUpdated.Value;
            return $"当前显示的是缓存数据（{WeatherFormat.DescribeAge(age)}更新）";
        }
    }

    /// <summary>既没有数据也没有错误（首次进入、尚未拉到结果）时给一句引导文案。</summary>
    public bool IsEmptyState => !HasCurrent && !HasError;

    public bool HasForecast => Forecast.Count > 0;

    public bool HasHourly => Hourly.Count > 0;

    public bool HasLifeIndex => LifeIndex.Count > 0 && _prefs.ShowLifeIndex;

    // ---------- 温度单位 ----------

    private bool _useFahrenheit;

    public bool UseFahrenheit
    {
        get => _useFahrenheit;
        private set
        {
            if (_useFahrenheit == value)
                return;

            _useFahrenheit = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UnitSymbol));
            RaiseTemperatureDependents();
        }
    }

    public string UnitSymbol => _useFahrenheit ? WeatherFormat.Fahrenheit : WeatherFormat.Celsius;

    /// <summary>单位切换后，把所有温度相关文本重新通知一遍。</summary>
    private void RaiseTemperatureDependents()
    {
        OnPropertyChanged(nameof(TemperatureText));
        OnPropertyChanged(nameof(FeelsLikeText));
        OnPropertyChanged(nameof(TrendRangeText));
    }

    // ---------- 派生展示文本 ----------

    public string TemperatureText => WeatherFormat.Temperature(Current.Temperature, _useFahrenheit);

    public string FeelsLikeText => WeatherFormat.Temperature(Current.FeelsLike, _useFahrenheit);

    public string AirQualityText => Current.HasAirQuality
        ? $"AQI {Current.Aqi} {(string.IsNullOrWhiteSpace(Current.AqiCategory) ? WeatherFormat.AqiCategory(Current.Aqi) : Current.AqiCategory)}"
        : string.Empty;

    public string AirQualityLevelText => Current.HasAirQuality
        ? (string.IsNullOrWhiteSpace(Current.AqiCategory) ? WeatherFormat.AqiCategory(Current.Aqi) : Current.AqiCategory)
        : string.Empty;

    public int AirQualityLevel => Current.HasAirQuality ? WeatherFormat.AqiLevel(Current.Aqi) : 0;

    public string WindText
    {
        get
        {
            if (!Current.HasWind && string.IsNullOrWhiteSpace(Current.WindDirection))
                return string.Empty;

            var direction = string.IsNullOrWhiteSpace(Current.WindDirection) ? "风向未知" : Current.WindDirection;
            return Current.HasWind ? $"{direction} {Current.WindSpeed:0.#} km/h" : direction;
        }
    }

    public string SunTimesText => Current.HasSunTimes
        ? $"日出 {Current.Sunrise} · 日落 {Current.Sunset}"
        : string.Empty;

    /// <summary>趋势图的温度区间摘要。图表是代码手绘的，这块文本同时作为无障碍描述和测试锚点。</summary>
    public string TrendRangeText
    {
        get
        {
            if (Forecast.Count == 0)
                return string.Empty;

            var min = Forecast.Min(f => f.TempLow);
            var max = Forecast.Max(f => f.TempHigh);
            return $"区间 {WeatherFormat.Temperature(min, _useFahrenheit)} ~ {WeatherFormat.Temperature(max, _useFahrenheit)}";
        }
    }

    /// <summary>预报区块的来源标注，如「来源：wttr.in」。按功能分配数据源后，各区块可能来自不同源。</summary>
    public string ForecastSourceText => SourceCaption(Forecast.FirstOrDefault()?.Source);

    /// <summary>逐小时区块的来源标注。</summary>
    public string HourlySourceText => SourceCaption(Hourly.FirstOrDefault()?.Source);

    private static string SourceCaption(string? source) =>
        string.IsNullOrWhiteSpace(source) ? string.Empty : $"来源：{source}";

    // ---------- 变更通知 ----------

    partial void OnCurrentChanged(WeatherCurrent value)
    {
        OnPropertyChanged(nameof(TemperatureText));
        OnPropertyChanged(nameof(FeelsLikeText));
        OnPropertyChanged(nameof(AirQualityText));
        OnPropertyChanged(nameof(AirQualityLevelText));
        OnPropertyChanged(nameof(AirQualityLevel));
        OnPropertyChanged(nameof(WindText));
        OnPropertyChanged(nameof(SunTimesText));
    }

    partial void OnErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsEmptyState));
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsNotLoading));

    partial void OnLastUpdatedChanged(DateTimeOffset? value) => OnPropertyChanged(nameof(CacheHint));

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool IsNotLoading => !IsLoading;

    private bool CanRefresh() => !IsLoading;

    // ---------- 命令 ----------

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshAsync()
    {
        ErrorMessage = string.Empty;
        IsLoading = true;

        try
        {
            Current = await _weatherService.GetCurrentWithFallbackAsync(City);
            LastUpdated = DateTimeOffset.Now;
            HasLoaded = true;
            HasCurrent = true;
            IsFromCache = false;

            await LoadExtrasAsync();
            SaveSnapshot();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 预报 / 逐小时 / 生活指数属于加分项：并行拉取，任一失败只影响自己那块，
    /// 不污染当前天气和错误提示。
    /// </summary>
    private async Task LoadExtrasAsync()
    {
        var forecastTask = SafeLoadAsync(() => _weatherService.GetForecastWithFallbackAsync(City, _forecastDays));
        var hourlyTask = SafeLoadAsync(() => _weatherService.GetHourlyWithFallbackAsync(City, _prefs.NormalizedHourlyCount));
        var lifeTask = SafeLoadAsync(() => _weatherService.GetLifeIndexWithFallbackAsync(City));

        await Task.WhenAll(forecastTask, hourlyTask, lifeTask).ConfigureAwait(true);

        ReplaceAll(Forecast, forecastTask.Result);
        ReplaceAll(Hourly, hourlyTask.Result);
        ReplaceAll(LifeIndex, lifeTask.Result);

        OnPropertyChanged(nameof(HasForecast));
        OnPropertyChanged(nameof(HasHourly));
        OnPropertyChanged(nameof(HasLifeIndex));
        OnPropertyChanged(nameof(TrendRangeText));
        OnPropertyChanged(nameof(ForecastSourceText));
        OnPropertyChanged(nameof(HourlySourceText));
    }

    private static async Task<List<T>> SafeLoadAsync<T>(Func<Task<IEnumerable<T>>> loader)
    {
        try
        {
            var items = await loader().ConfigureAwait(false);
            return items?.ToList() ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static void ReplaceAll<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }

    // ---------- 单位与偏好 ----------

    /// <summary>
    /// 切换温度单位。会重建预报/逐小时的集合内容，
    /// 让 DataTemplate 里的绑定重新求值（否则列表项会保持旧单位的文本）。
    /// </summary>
    public void ApplyUnit(bool useFahrenheit, bool persist = true)
    {
        if (_prefs.UseFahrenheit != useFahrenheit)
        {
            _prefs.UseFahrenheit = useFahrenheit;
            if (persist)
                _preferences?.Save(_prefs);
        }

        UnitSettings.UseFahrenheit = useFahrenheit;
        UseFahrenheit = useFahrenheit;

        RebuildForUnitChange(Forecast);
        RebuildForUnitChange(Hourly);
    }

    private static void RebuildForUnitChange<T>(ObservableCollection<T> source)
    {
        if (source.Count == 0)
            return;

        var snapshot = source.ToList();
        source.Clear();
        foreach (var item in snapshot)
            source.Add(item);
    }

    /// <summary>保存偏好并立即生效（设置页调用）。</summary>
    public void ApplyPreferences(AppPreferences preferences)
    {
        _prefs = preferences;
        _preferences?.Save(_prefs);

        UnitSettings.UseFahrenheit = _prefs.UseFahrenheit;
        UseFahrenheit = _prefs.UseFahrenheit;

        RebuildForUnitChange(Forecast);
        RebuildForUnitChange(Hourly);

        OnPropertyChanged(nameof(HasLifeIndex));
    }

    public AppPreferences Preferences => _prefs;

    // ---------- 缓存 ----------

    private void RestoreFromCache()
    {
        var snapshot = _cache?.Load();
        if (snapshot?.Current is null)
            return;

        City = string.IsNullOrWhiteSpace(snapshot.City) ? City : snapshot.City;
        Current = snapshot.Current;
        LastUpdated = snapshot.SavedAt;
        HasCurrent = true;
        IsFromCache = true;

        ReplaceAll(Forecast, snapshot.Forecast);
        ReplaceAll(Hourly, snapshot.Hourly);
        ReplaceAll(LifeIndex, snapshot.LifeIndex);

        OnPropertyChanged(nameof(HasForecast));
        OnPropertyChanged(nameof(HasHourly));
        OnPropertyChanged(nameof(HasLifeIndex));
        OnPropertyChanged(nameof(ForecastSourceText));
        OnPropertyChanged(nameof(HourlySourceText));
    }

    private void SaveSnapshot()
    {
        if (_cache is null || !HasCurrent)
            return;

        _cache.Save(new WeatherSnapshot
        {
            City = City,
            Current = Current,
            Forecast = Forecast.ToList(),
            Hourly = Hourly.ToList(),
            LifeIndex = LifeIndex.ToList(),
            SavedAt = LastUpdated ?? DateTimeOffset.Now,
        });
    }
}
