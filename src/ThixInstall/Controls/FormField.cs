using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace ThixInstall.Controls;

/// <summary>
/// A labelled input with an optional hint and an error line. <see cref="Field"/> is the name the business layer uses in
/// <c>BusinessException.Field</c>: <see cref="ShowError"/> finds the right field on a page by it.
/// </summary>
public sealed class FormField : ContentControl
{
    public static readonly StyledProperty<string> LabelProperty = AvaloniaProperty.Register<FormField, string>(nameof(Label), "");

    public static readonly StyledProperty<string?> HintProperty = AvaloniaProperty.Register<FormField, string?>(nameof(Hint));

    public static readonly StyledProperty<string?> ErrorProperty = AvaloniaProperty.Register<FormField, string?>(nameof(Error));

    public static readonly StyledProperty<string?> FieldProperty = AvaloniaProperty.Register<FormField, string?>(nameof(Field));

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public string? Error
    {
        get => GetValue(ErrorProperty);
        set => SetValue(ErrorProperty, value);
    }

    public string? Field
    {
        get => GetValue(FieldProperty);
        set => SetValue(FieldProperty, value);
    }

    /// <summary>Clears every error inside <paramref name="root"/>, then shows the message at the field it names. False when no field matched.</summary>
    public static bool ShowError(Control root, string? field, string message)
    {
        var fields = root.GetVisualDescendants().OfType<FormField>().ToList();
        foreach (var formField in fields)
        {
            formField.Error = null;
        }

        var target = field is null ? null : fields.FirstOrDefault(formField => string.Equals(formField.Field, field, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return false;
        }

        target.Error = message;
        return true;
    }
}
