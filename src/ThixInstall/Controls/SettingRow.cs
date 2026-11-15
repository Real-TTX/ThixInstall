using Avalonia;
using Avalonia.Controls;

namespace ThixInstall.Controls;

/// <summary>
/// One setting: title and a short description at the left, its control (switch, choice, button) at the right.
/// The look is the ControlTheme in Themes/Theme.axaml.
/// </summary>
public sealed class SettingRow : ContentControl
{
    public static readonly StyledProperty<string> TitleProperty = AvaloniaProperty.Register<SettingRow, string>(nameof(Title), "");

    public static readonly StyledProperty<string?> DescriptionProperty = AvaloniaProperty.Register<SettingRow, string?>(nameof(Description));

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
}
