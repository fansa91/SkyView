using Microsoft.UI.Dispatching;
using SkyView.Models;
using SkyView.Services;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SkyView.ViewModels;

/// <summary>
/// 城市天气卡片的视图模型：一张卡片对应一个城市，展示「当天天气」快照。
/// <para>
/// 数据由页面后台并发调用 <c>IWeatherService.GetCurrentWithFallbackAsync</c> 填充，
/// 填充期间显示 <see cref="IsLoading"/>，失败显示 <see cref="Error"/>。
/// 计算属性（<see cref="HasData"/> / <see cref="DisplayTemperature"/> / <see cref="DetailLine"/>）
/// 随底层数据变化一并刷新，供 XAML 绑定。
/// </para>
/// </summary>
public sealed class CityWeatherCard : INotifyPropertyChanged
{
    public string City { get; }

    public CityWeatherCard(string city) => City = city;

    private WeatherCurrent? _current;
    public WeatherCurrent? Current
    {
        get => _current;
        set { _current = value; Raise(); }
    }

    private bool _isLoading = true;
    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; Raise(); }
    }

    private string? _error;
    public string? Error
    {
        get => _error;
        set { _error = value; Raise(); }
    }

    /// <summary>有有效数据且不在加载、无错误时为真。</summary>
    public bool HasData => !IsLoading && _error is null && _current is not null;

    /// <summary>当前温度，按全局单位设置显示（如「13.4°C」/「58.5°F」）。</summary>
    public string DisplayTemperature => _current is null
        ? string.Empty
        : $"{UnitSettings.Convert(_current.Temperature):0.#}{UnitSettings.Symbol}";

    /// <summary>次要信息行：湿度、风力，用「 · 」连接。</summary>
    public string DetailLine
    {
        get
        {
            if (_current is null)
                return string.Empty;

            var parts = new List<string>();
            if (_current.Humidity > 0)
                parts.Add($"湿度 {_current.Humidity}%");
            if (_current.HasWind)
                parts.Add($"{_current.WindDirection} {_current.WindSpeed:0.#}km/h");
            return string.Join(" · ", parts);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? propertyName = null)
    {
        // 底层数据/状态变化时，计算属性也要重算，否则 XAML 不会刷新
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasData)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayTemperature)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailLine)));
    }

    /// <summary>在 UI 线程上变更属性（数据回填发生在后台 await 之后，可能不在 UI 线程）。</summary>
    public void UpdateOnUiThread(DispatcherQueue? dispatcher, System.Action<CityWeatherCard> mutate)
    {
        if (dispatcher is null)
        {
            mutate(this);
            return;
        }

        dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, () => mutate(this));
    }
}
