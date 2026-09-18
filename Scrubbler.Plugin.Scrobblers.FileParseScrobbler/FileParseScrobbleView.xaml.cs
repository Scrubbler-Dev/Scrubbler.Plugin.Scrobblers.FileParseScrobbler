// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace Scrubbler.Plugin.Scrobbler.FileParseScrobbler;

public sealed partial class FileParseScrobbleView : UserControl
{
    public FileParseScrobbleView()
    {
        this.InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is FileParseScrobbleViewModel vm) await vm.RefreshImportsCommand.ExecuteAsync(null);
        };
    }
}
