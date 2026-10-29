namespace SkyView.Icons;

/// <summary>
/// 天气图标的语义分类。刻意和「数据源给的原始字符串」解耦：
/// 上游 <c>MapCondition</c> 已经把各家的描述归成中文短词，这里只关心画面要画什么。
/// </summary>
public enum WeatherIconKind
{
    /// <summary>无法判断。画一朵中性云。</summary>
    Unknown = 0,

    /// <summary>晴。</summary>
    Clear,

    /// <summary>多云 / 晴间多云。</summary>
    PartlyCloudy,

    /// <summary>阴 / 阴天。</summary>
    Cloudy,

    /// <summary>小雨 / 毛毛雨 / 细雨。</summary>
    LightRain,

    /// <summary>雨（未标注强度时）。</summary>
    Rain,

    /// <summary>大雨 / 暴雨 / 强降雨。</summary>
    HeavyRain,

    /// <summary>雷阵雨 / 雷暴。</summary>
    Thunderstorm,

    /// <summary>雪 / 阵雪 / 雨夹雪。</summary>
    Snow,

    /// <summary>雾 / 霾 / 沙尘暴以外的低能见度。</summary>
    Fog,

    /// <summary>沙尘 / 浮尘 / 扬沙。</summary>
    Dust,

    /// <summary>大风。</summary>
    Wind,
}
