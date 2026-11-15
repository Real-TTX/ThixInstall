using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ThixInstall.Controls;

/// <summary>
/// The icon of an app in the catalog: its picture on a white rounded tile, or - without one - its first letter on a
/// coloured tile (the colour follows the app's key, so it stays the same everywhere). One code base for tiles, rows and
/// the bundle window.
/// </summary>
public sealed class AppIcon : Border
{
    public static readonly StyledProperty<string> NameTextProperty = AvaloniaProperty.Register<AppIcon, string>(nameof(NameText), "");

    public static readonly StyledProperty<string> KeyProperty = AvaloniaProperty.Register<AppIcon, string>(nameof(Key), "");

    public static readonly StyledProperty<Bitmap?> ImageProperty = AvaloniaProperty.Register<AppIcon, Bitmap?>(nameof(Image));

    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<AppIcon, double>(nameof(Size), 44);

    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _letter = new() { Foreground = Brushes.White, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    static AppIcon()
    {
        NameTextProperty.Changed.AddClassHandler<AppIcon>((icon, _) => icon.Refresh());
        KeyProperty.Changed.AddClassHandler<AppIcon>((icon, _) => icon.Refresh());
        ImageProperty.Changed.AddClassHandler<AppIcon>((icon, _) => icon.Refresh());
        SizeProperty.Changed.AddClassHandler<AppIcon>((icon, _) => icon.Refresh());
    }

    public AppIcon()
    {
        Child = new Panel { Children = { _letter, _image } };
        Refresh();
    }

    public string NameText
    {
        get => GetValue(NameTextProperty);
        set => SetValue(NameTextProperty, value);
    }

    public string Key
    {
        get => GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    public Bitmap? Image
    {
        get => GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    private void Refresh()
    {
        Width = Height = Size;
        CornerRadius = new CornerRadius(Size * 0.24);
        ClipToBounds = true;
        var hasImage = Image is not null;
        _image.Source = Image;
        _image.IsVisible = hasImage;
        _image.Margin = new Thickness(Size * 0.12);
        _letter.IsVisible = !hasImage;
        _letter.Text = PackageIcons.Letter(NameText);
        _letter.FontSize = Size * 0.46;
        Background = hasImage ? Brushes.White : PackageIcons.LetterBrush(Key.Length > 0 ? Key : NameText);
        BorderBrush = hasImage ? new SolidColorBrush(Color.FromArgb(28, 0, 0, 0)) : null;
        BorderThickness = new Thickness(hasImage ? 1 : 0);
    }
}
