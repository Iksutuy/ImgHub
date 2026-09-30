using ImgHub.Core.Models;

namespace ImgHub.App.ViewModels;

/// <summary>顶栏 provider 下拉选项（独立类型保证 ItemsSource/SelectedItem 类型一致）。</summary>
public sealed class ProviderOption
{
    public ApiProvider Provider { get; }
    public string Label { get; }

    public ProviderOption(ApiProvider provider, string label)
    {
        Provider = provider;
        Label = label;
    }

    public override string ToString() => Label;
}
