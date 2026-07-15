using System.Windows;
using VideoSplitJoiner.App.ViewModels;

namespace VideoSplitJoiner.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}
