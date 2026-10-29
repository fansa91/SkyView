namespace SkyView.Models;

/// <summary>空气质量原始结果（由 Provider 返回，再由 WeatherService 合并进 WeatherCurrent）。</summary>
public class AirQuality
{
    public int Aqi { get; set; }
    public string Category { get; set; } = string.Empty;
    public string PrimaryPollutant { get; set; } = string.Empty;
    public double Pm25 { get; set; }
}
