using System.ComponentModel;
using System.Runtime.CompilerServices;
using GwsBusinessSuite.SentinelAgentKit;

namespace GwsBusinessSuite.App;

// One row in the model library. Mutable and observable rather than a plain record because a row
// changes state in place while its download runs ("Install" -> "12%" -> "Installed") - rebuilding
// the whole list on each progress line would fight the CollectionView for scroll position.
public sealed class BrowsableModelViewModel(SuggestedModel model, bool isInstalled) : INotifyPropertyChanged
{
    private bool _isInstalled = isInstalled;
    private string? _progress;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name => model.Name;

    public string Description => model.Description;

    // Tool support is stated on every row, not just the ones lacking it: "no tools" only reads
    // as a warning if "tools" is visible elsewhere for contrast.
    public string Headline =>
        $"{model.Name}  ·  {model.ApproximateSize}  ·  {(model.SupportsTools ? "tools" : "no tools")}";

    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (_isInstalled == value) return;
            _isInstalled = value;
            Raise(nameof(IsInstalled));
            Raise(nameof(ActionText));
            Raise(nameof(CanInstall));
        }
    }

    public string? Progress
    {
        get => _progress;
        set
        {
            if (_progress == value) return;
            _progress = value;
            Raise(nameof(Progress));
            Raise(nameof(ActionText));
            Raise(nameof(CanInstall));
        }
    }

    public string ActionText => _progress ?? (_isInstalled ? "Installed" : "Install");

    // Disabled both while a download is in flight and once the model is present, so the same
    // button can't queue a second pull of something already arriving.
    public bool CanInstall => _progress is null && !_isInstalled;

    private void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
