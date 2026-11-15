using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ThixInstall.Controls;

public enum ChipTone
{
    Neutral,
    Accent,
    Success,
    Warning,
    Danger,
}

/// <summary>
/// A small state in a row or tile ("Installiert", "Update", "Fehlgeschlagen"): text on a soft tint of its tone, always
/// with words (the colour is never the only signal). One code base for every list of the app.
/// </summary>
public sealed class StatusChip : Border
{
    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<StatusChip, string>(nameof(Text), "");

    public static readonly StyledProperty<ChipTone> ToneProperty = AvaloniaProperty.Register<StatusChip, ChipTone>(nameof(Tone));

    private readonly TextBlock _text = new() { FontSize = 11.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };

    static StatusChip()
    {
        TextProperty.Changed.AddClassHandler<StatusChip>((chip, _) => chip.Refresh());
        ToneProperty.Changed.AddClassHandler<StatusChip>((chip, _) => chip.Refresh());
    }

    public StatusChip()
    {
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(7, 2);
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Left;
        Child = _text;
        ActualThemeVariantChanged += (_, _) => Refresh();
        Refresh();
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public ChipTone Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    private void Refresh()
    {
        _text.Text = Text;
        IsVisible = Text.Length > 0;
        var (background, foreground) = Tone switch
        {
            ChipTone.Accent => ("AccentSoftBrush", "AccentTextBrush"),
            ChipTone.Success => ("SuccessSoftBrush", "SuccessBrush"),
            ChipTone.Warning => ("WarningSoftBrush", "WarningBrush"),
            ChipTone.Danger => ("DangerSoftBrush", "DangerBrush"),
            _ => ("SubtleBrush", "TextMutedBrush"),
        };
        if (Application.Current is null)
        {
            return;
        }

        Background = Ui.Brush(background);
        _text.Foreground = Ui.Brush(foreground);
    }
}
