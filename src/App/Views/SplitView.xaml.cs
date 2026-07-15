using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VideoSplitJoiner.App.ViewModels;

namespace VideoSplitJoiner.App.Views;

/// <summary>
/// Code-behind for the Split screen. Keeps only the file-dialog plumbing here (a GUI concern);
/// all split logic lives in <see cref="SplitViewModel"/>. The "Load…" button opens an
/// <see cref="OpenFileDialog"/> and forwards the chosen path to the VM's <c>LoadCommand</c>.
/// </summary>
public partial class SplitView : UserControl
{
    public SplitView()
    {
        InitializeComponent();
    }

    private void OnLoadClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SplitViewModel vm)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select a video to split",
            Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.ts|All files|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true)
        {
            vm.LoadCommand.Execute(dialog.FileName);
        }
    }
}
