using System.Windows;
using System.Windows.Media;

namespace Rambla.Demo.MarketDashboard;

public partial class MainWindow : Window
{
    private readonly DashboardViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new DashboardViewModel(Dispatcher);
        DataContext = _viewModel;

        // Sample producer -> visible latency once per rendered frame.
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e) => _viewModel.OnRendering();

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        try
        {
            await _viewModel.StartAsync();
        }
        finally
        {
            StartButton.IsEnabled = true;
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
        => await _viewModel.StopAsync();

    protected override async void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        await _viewModel.StopAsync();
        base.OnClosed(e);
    }
}
