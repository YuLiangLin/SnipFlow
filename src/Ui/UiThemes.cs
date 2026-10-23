using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using SnipFlow.Services;

namespace SnipFlow.Ui;

/// <summary>Code-side access to the keyed control themes in App.axaml (the WPF keyed styles).</summary>
public static class UiThemes
{
    /// <summary>Keys: QuietButton, NavigationButton, WindowControl, Primary, ColorSwatch, ToggleSwitch.</summary>
    public static ControlTheme Get(string key)
    {
        if (Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is ControlTheme theme)
            return theme;
        throw new KeyNotFoundException($"Control theme '{key}' is not defined in App.axaml.");
    }

    /// <summary>Applies a keyed control theme and returns the control for object-initializer style use.</summary>
    public static T WithTheme<T>(this T control, string key) where T : StyledElement
    {
        control.Theme = Get(key);
        return control;
    }

    public static IBrush Brush(string key) => ThemeService.Brush(key);

    public static FontFamily AppFont => Font("AppFont");
    public static FontFamily MonoFont => Font("MonoFont");

    static FontFamily Font(string key) => Application.Current is { } app
        && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is FontFamily family
        ? family : FontFamily.Default;
}

/// <summary>Helpers for frameless windows drawn with their own title bar, as in the WPF version.</summary>
public static class WindowChrome
{
    /// <summary>Lets <paramref name="titleBar"/> move the window; double-click toggles maximize when allowed.</summary>
    public static void EnableTitleBarDrag(Window window, Control titleBar, bool allowMaximize = true)
    {
        titleBar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(titleBar).Properties.IsLeftButtonPressed) return;
            if (e.Source is Visual source && IsInsideButton(source, titleBar)) return;
            if (e.ClickCount == 2 && allowMaximize && window.CanResize)
            {
                window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                e.Handled = true;
                return;
            }
            window.BeginMoveDrag(e);
            e.Handled = true;
        };
    }

    /// <summary>Adds an invisible resize border to a frameless, resizable window.</summary>
    public static void EnableResizeBorder(Window window, double thickness = 6)
    {
        WindowEdge? EdgeAt(Point point)
        {
            if (!window.CanResize || window.WindowState != WindowState.Normal) return null;
            var size = window.Bounds.Size;
            bool left = point.X < thickness, right = point.X > size.Width - thickness;
            bool top = point.Y < thickness, bottom = point.Y > size.Height - thickness;
            return (left, right, top, bottom) switch
            {
                (true, _, true, _) => WindowEdge.NorthWest,
                (_, true, true, _) => WindowEdge.NorthEast,
                (true, _, _, true) => WindowEdge.SouthWest,
                (_, true, _, true) => WindowEdge.SouthEast,
                (true, _, _, _) => WindowEdge.West,
                (_, true, _, _) => WindowEdge.East,
                (_, _, true, _) => WindowEdge.North,
                (_, _, _, true) => WindowEdge.South,
                _ => null,
            };
        }
        static StandardCursorType CursorFor(WindowEdge edge) => edge switch
        {
            WindowEdge.NorthWest => StandardCursorType.TopLeftCorner,
            WindowEdge.NorthEast => StandardCursorType.TopRightCorner,
            WindowEdge.SouthWest => StandardCursorType.BottomLeftCorner,
            WindowEdge.SouthEast => StandardCursorType.BottomRightCorner,
            WindowEdge.West => StandardCursorType.LeftSide,
            WindowEdge.East => StandardCursorType.RightSide,
            WindowEdge.North => StandardCursorType.TopSide,
            _ => StandardCursorType.BottomSide,
        };
        Cursor? edgeCursor = null;
        window.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            var edge = EdgeAt(e.GetPosition(window));
            if (edge is { } value)
            {
                edgeCursor = new Cursor(CursorFor(value));
                window.Cursor = edgeCursor;
            }
            else if (edgeCursor is not null)
            {
                window.Cursor = null;
                edgeCursor = null;
            }
        }, RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) return;
            if (EdgeAt(e.GetPosition(window)) is not { } edge) return;
            window.BeginResizeDrag(edge, e);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    static bool IsInsideButton(Visual source, Visual stop)
    {
        for (Visual? node = source; node is not null && !ReferenceEquals(node, stop); node = node.GetVisualParent())
            if (node is Button or TextBox or ComboBox or ToggleButton) return true;
        return false;
    }
}
