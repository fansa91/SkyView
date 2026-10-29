using CommunityToolkit.Mvvm.ComponentModel;

namespace SkyView.Models;

/// <summary>设置页里一个数据源的可编辑状态。</summary>
public partial class ProviderConfigItem : ObservableObject
{
    public ProviderConfigItem()
    {
        Name = string.Empty;
        DisplayName = string.Empty;
        ApiHost = string.Empty;
        SavedApiKey = string.Empty;
        ApiKeyInput = string.Empty;
        TestMessage = string.Empty;
    }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string DisplayName { get; set; }

    [ObservableProperty]
    public partial string ApiHost { get; set; }

    /// <summary>已经保存在本地的 Key（不回显到界面上，只用于"留空则不变"的判断）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSavedKey))]
    [NotifyPropertyChangedFor(nameof(ApiKeyHint))]
    public partial string SavedApiKey { get; set; }

    /// <summary>用户在密码框里新输入的 Key。</summary>
    [ObservableProperty]
    public partial string ApiKeyInput { get; set; }

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial double Priority { get; set; }

    // ---------- 测试连接 ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult))]
    public partial string TestMessage { get; set; }

    /// <summary>null = 还没测过；true = 成功；false = 失败。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult))]
    [NotifyPropertyChangedFor(nameof(TestSucceededText))]
    public partial bool? TestSucceeded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTest))]
    public partial bool IsTesting { get; set; }

    /// <summary>测试按钮可用性：正在测试时禁用，避免重复点。</summary>
    public bool CanTest => !IsTesting;

    public bool HasTestResult => !string.IsNullOrWhiteSpace(TestMessage);

    public string TestSucceededText => TestSucceeded switch
    {
        true => "连接成功",
        false => "连接失败",
        null => string.Empty,
    };

    public bool HasSavedKey => !string.IsNullOrWhiteSpace(SavedApiKey);

    public string ApiKeyHint => HasSavedKey ? "已保存（留空则保持不变）" : "尚未配置";

    /// <summary>保存时决定最终 Key：输入非空则覆盖，否则沿用旧值。</summary>
    public string ResolveApiKey() =>
        string.IsNullOrWhiteSpace(ApiKeyInput) ? SavedApiKey : ApiKeyInput.Trim();

    /// <summary>把界面上的编辑状态转成数据源配置（测试连接时也要用未保存的输入）。</summary>
    public ProviderConfig ToConfig() => new()
    {
        Name = Name,
        DisplayName = DisplayName,
        ApiHost = ApiHost?.Trim() ?? string.Empty,
        ApiKey = ResolveApiKey(),
        Enabled = Enabled,
        Priority = (int)System.Math.Round(Priority),
    };
}
