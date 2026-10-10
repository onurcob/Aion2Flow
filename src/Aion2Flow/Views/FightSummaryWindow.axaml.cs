using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Cloris.Aion2Flow.ViewModels;

namespace Cloris.Aion2Flow.Views;

public partial class FightSummaryWindow : Window
{
    public FightSummaryWindow()
    {
        InitializeComponent();
    }

    public FightSummaryWindow(FightSummaryViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as IDisposable)?.Dispose();
        base.OnClosed(e);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        FitToScreen();
    }

    // The header and any empty area of the window move it; buttons, the scroll bar
    // and other controls handle their own presses first, so they keep working.
    private void HeaderPointerPressed(object? sender, PointerPressedEventArgs e) => BeginMove(e);

    private void RootPointerPressed(object? sender, PointerPressedEventArgs e) => BeginMove(e);

    private void BottomEdgePointerPressed(object? sender, PointerPressedEventArgs e) => BeginResize(WindowEdge.South, e);

    private void CornerPointerPressed(object? sender, PointerPressedEventArgs e) => BeginResize(WindowEdge.SouthEast, e);

    private void BeginMove(PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        e.Handled = true;
        BeginMoveDrag(e);
    }

    private void BeginResize(WindowEdge edge, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        e.Handled = true;
        BeginResizeDrag(edge, e);
    }

    /// <summary>Keeps the window inside the screen it opened on, so its bottom edge stays reachable.</summary>
    private void FitToScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
            return;

        var scale = screen.Scaling > 0 ? screen.Scaling : 1d;
        var area = screen.WorkingArea;
        var maxHeight = area.Height / scale - 24d;
        if (Height > maxHeight)
            Height = Math.Max(MinHeight, maxHeight);

        var heightPx = (int)Math.Round(Height * scale);
        var widthPx = (int)Math.Round(Width * scale);
        var x = Math.Clamp(Position.X, area.X, Math.Max(area.X, area.Right - widthPx));
        var y = Math.Clamp(Position.Y, area.Y, Math.Max(area.Y, area.Bottom - heightPx));
        Position = new Avalonia.PixelPoint(x, y);
    }

    private void CloseClicked(object? sender, RoutedEventArgs e) => Close();
}
