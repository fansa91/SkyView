namespace SkyView.Models;

/// <summary>生活指数（穿衣、紫外线、运动等）。</summary>
public class WeatherLifeIndex
{
    /// <summary>指数名称，如「穿衣指数」。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>等级文本，如「较冷」。</summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>建议正文。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>界面用的简短组合，如「穿衣指数 · 较冷」。</summary>
    public string Summary => string.IsNullOrEmpty(Level) ? Name : $"{Name} · {Level}";

    public bool HasText => !string.IsNullOrWhiteSpace(Text);
}
