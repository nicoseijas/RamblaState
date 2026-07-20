using System.Windows;

namespace Rambla.Demo.MarketDashboard;

public partial class MainWindow : Window
{
    private readonly DashboardViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new DashboardViewModel(Dispatcher);
        DataContext = _viewModel;
    }

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
        await _viewModel.StopAsync();
        base.OnClosed(e);
    }
}
