using VideoSplitJoiner.Core;

namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// Root view model for the main window. Placeholder for T-001 — real
/// split/join view models arrive in later tickets.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private string _title = $"{AppInfo.Name} — Split / Join";

    /// <summary>Window title text, bindable from the view.</summary>
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }
}
