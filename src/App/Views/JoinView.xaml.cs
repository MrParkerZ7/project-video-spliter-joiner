using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VideoSplitJoiner.App.ViewModels;

namespace VideoSplitJoiner.App.Views;

/// <summary>
/// Code-behind for the Join screen. Keeps only the file-dialog plumbing here (a GUI concern);
/// all join logic lives in <see cref="JoinViewModel"/>. The "Add files…" button opens a
/// multiselect <see cref="OpenFileDialog"/> and forwards the chosen paths to the VM's
/// <c>AddFilesCommand</c>.
/// </summary>
public partial class JoinView : UserControl
{
    public JoinView()
    {
        InitializeComponent();
    }

    private void OnAddFilesClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not JoinViewModel vm)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select videos to join",
            Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.ts|All files|*.*",
            CheckFileExists = true,
            Multiselect = true,
        };

        if (dialog.ShowDialog() == true)
        {
            vm.AddFilesCommand.Execute(dialog.FileNames);
        }
    }
}
