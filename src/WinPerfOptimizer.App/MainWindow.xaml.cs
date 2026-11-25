using WinPerfOptimizer.App.ViewModels;
using Wpf.Ui.Controls;

namespace WinPerfOptimizer.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
