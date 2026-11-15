using Avalonia.Controls;
using Avalonia.Layout;

namespace ThixInstall.Controls;

/// <summary>
/// The toolbar above every list: filter, category, sort and view side by side, in one line while there is room (it
/// wraps in a narrow window, order unchanged). The search of a list is the field in the window's header.
/// </summary>
public sealed class Toolbar : WrapPanel
{
    public Toolbar()
    {
        Orientation = Orientation.Horizontal;
        ItemSpacing = 8;
        LineSpacing = 8;
        Margin = new Avalonia.Thickness(0, 0, 0, 14);
    }
}
