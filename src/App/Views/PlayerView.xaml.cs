using System.Windows;
using System.Windows.Controls;
using VideoSplitJoiner.App.Media;
using VideoSplitJoiner.App.ViewModels;

namespace VideoSplitJoiner.App.Views;

/// <summary>
/// Code-behind for the video preview player. Keeps only the WPF seam: on <c>Loaded</c> it hands the
/// view's FFME <see cref="Unosquare.FFME.MediaElement"/> to the <see cref="FfmeMediaPlayer"/> that
/// backs the bound <see cref="PlayerViewModel"/>. All transport/timeline logic lives WPF-free in the
/// VM. If the VM's player is not a <see cref="FfmeMediaPlayer"/> (e.g. a design-time or test
/// stand-in) the attach is skipped silently.
/// </summary>
public partial class PlayerView : UserControl
{
    private bool _attached;

    public PlayerView()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_attached || DataContext is not PlayerViewModel vm)
        {
            return;
        }

        if (vm.PlayerControl is FfmeMediaPlayer ffmePlayer)
        {
            ffmePlayer.Attach(Media);
            _attached = true;
        }
    }
}
