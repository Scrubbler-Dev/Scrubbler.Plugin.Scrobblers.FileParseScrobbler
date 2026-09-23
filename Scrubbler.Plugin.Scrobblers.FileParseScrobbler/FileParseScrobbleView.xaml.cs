// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace Scrubbler.Plugin.Scrobbler.FileParseScrobbler;

public sealed partial class FileParseScrobbleView : UserControl
{
    private void IntegerValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        // NumberBox validates text and bounds; enforce whole numbers and restore
        // the previous value when the field is cleared.
        var value = double.IsFinite(args.NewValue) ? Math.Round(args.NewValue, MidpointRounding.AwayFromZero)
            : double.IsFinite(args.OldValue) ? args.OldValue : sender.Minimum;
        value = Math.Clamp(value, sender.Minimum, sender.Maximum);
        if (sender.Value != value) sender.Value = value;
    }

    public FileParseScrobbleView()
    {
        this.InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is FileParseScrobbleViewModel vm) await vm.RefreshImportsCommand.ExecuteAsync(null);
        };
    }
}
