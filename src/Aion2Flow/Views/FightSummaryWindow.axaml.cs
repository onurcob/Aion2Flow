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

    private void HeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            e.Handled = true;
            BeginMoveDrag(e);
        }
    }

    private void CloseClicked(object? sender, RoutedEventArgs e) => Close();
}
