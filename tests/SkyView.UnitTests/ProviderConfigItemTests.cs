using SkyView.Models;
using Xunit;

namespace SkyView.UnitTests;

public class ProviderConfigItemTests
{
    [Fact]
    public void ResolveApiKey_ShouldKeepSavedKey_WhenInputIsEmpty()
    {
        var item = new ProviderConfigItem { SavedApiKey = "saved-key", ApiKeyInput = string.Empty };

        Assert.Equal("saved-key", item.ResolveApiKey());
    }

    [Fact]
    public void ResolveApiKey_ShouldUseInput_WhenProvided()
    {
        var item = new ProviderConfigItem { SavedApiKey = "saved-key", ApiKeyInput = "  new-key  " };

        Assert.Equal("new-key", item.ResolveApiKey());
    }

    [Fact]
    public void ResolveApiKey_ShouldReturnEmpty_WhenNothingConfigured()
    {
        var item = new ProviderConfigItem();

        Assert.Equal(string.Empty, item.ResolveApiKey());
    }

    [Fact]
    public void ApiKeyHint_ShouldReflectSavedState()
    {
        var item = new ProviderConfigItem();
        Assert.False(item.HasSavedKey);
        Assert.Equal("尚未配置", item.ApiKeyHint);

        item.SavedApiKey = "abc";
        Assert.True(item.HasSavedKey);
        Assert.Contains("已保存", item.ApiKeyHint);
    }

    [Fact]
    public void ApiKeyHint_ShouldRaisePropertyChanged_WhenSavedKeyChanges()
    {
        var item = new ProviderConfigItem();
        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        item.SavedApiKey = "abc";

        Assert.Contains(nameof(ProviderConfigItem.ApiKeyHint), raised);
        Assert.Contains(nameof(ProviderConfigItem.HasSavedKey), raised);
    }
}
