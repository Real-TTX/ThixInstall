using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace ThixInstall.Controls;

/// <summary>
/// Draws one of the line icons (Themes/Icons.axaml, a 24 x 24 grid) in the current text colour: stroked with a round
/// 2 px line like MatDesign's icons, or filled (a chosen star). The size is the control's size.
/// </summary>
public sealed class IconView : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<IconView, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<IconView>();

    public static readonly StyledProperty<bool> IsFilledProperty = AvaloniaProperty.Register<IconView, bool>(nameof(IsFilled));

    public static readonly StyledProperty<double> StrokeWidthProperty = AvaloniaProperty.Register<IconView, double>(nameof(StrokeWidth), 2);

    static IconView()
    {
        AffectsRender<IconView>(DataProperty, ForegroundProperty, IsFilledProperty, StrokeWidthProperty);
        WidthProperty.OverrideDefaultValue<IconView>(18);
        HeightProperty.OverrideDefaultValue<IconView>(18);
        VerticalAlignmentProperty.OverrideDefaultValue<IconView>(Avalonia.Layout.VerticalAlignment.Center);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public bool IsFilled
    {
        get => GetValue(IsFilledProperty);
        set => SetValue(IsFilledProperty, value);
    }

    public double StrokeWidth
    {
        get => GetValue(StrokeWidthProperty);
        set => SetValue(StrokeWidthProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Data is null || Foreground is null)
        {
            return;
        }

        var side = Math.Min(Bounds.Width, Bounds.Height);
        var scale = side / 24.0;
        var offset = new Vector((Bounds.Width - side) / 2, (Bounds.Height - side) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset)))
        {
            var pen = new Pen(Foreground, StrokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            context.DrawGeometry(IsFilled ? Foreground : null, pen, Data);
        }
    }
}
