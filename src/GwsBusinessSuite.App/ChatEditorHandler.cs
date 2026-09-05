using Microsoft.Maui.Handlers;

namespace GwsBusinessSuite.App;

// Per-platform Return handling for ChatEditor. Each platform gets the approach its input stack
// actually supports, rather than one shared abstraction that works properly on none of them.
public partial class ChatEditorHandler : EditorHandler
{
    public ChatEditorHandler() : base(Mapper, CommandMapper)
    {
    }
}

#if IOS || MACCATALYST

// On UIKit a hardware Return reaches the text view as ordinary text input ("\n") with no
// modifier information attached, so Shift+Return cannot be told apart at that point. The split
// is therefore:
//
//   Shift+Return - declared as a UIKeyCommand. Modified key combinations are offered to the
//                  responder chain's key commands before text input, so this branch runs
//                  instead of the default insertion, and inserts the newline itself.
//   Return       - no key command matches, so it arrives as text input and ShouldChangeText
//                  turns it into a send.
//
// PressesBegan is deliberately avoided: it is documented as unreliable for text keys on Mac
// Catalyst (dotnet/maui#9838), which is exactly the case this needs.
public partial class ChatEditorHandler
{
    protected override Microsoft.Maui.Platform.MauiTextView CreatePlatformView()
    {
        var textView = new SendOnReturnTextView();
        textView.ShouldChangeText = (_, _, replacement) =>
        {
            if (replacement != "\n") return true;
            if (VirtualView is ChatEditor editor) editor.RaiseSendRequested();
            return false;
        };
        textView.NewlineRequested += () =>
        {
            // Insert at the caret rather than appending, so Shift+Return works mid-message.
            var range = textView.SelectedRange;
            var text = textView.Text ?? string.Empty;
            textView.Text = text[..(int)range.Location] + "\n" + text[((int)range.Location + (int)range.Length)..];
            textView.SelectedRange = new Foundation.NSRange(range.Location + 1, 0);
            // MAUI syncs from the platform view's Changed event, which a programmatic Text set
            // does not raise - push the value across explicitly or the binding goes stale.
            if (VirtualView is ChatEditor editor) editor.Text = textView.Text;
        };
        return textView;
    }
}

public sealed class SendOnReturnTextView : Microsoft.Maui.Platform.MauiTextView
{
    public event Action? NewlineRequested;

    public override UIKit.UIKeyCommand[] KeyCommands =>
    [
        UIKit.UIKeyCommand.Create(
            title: new Foundation.NSString("New line"),
            image: null,
            action: new ObjCRuntime.Selector(nameof(InsertNewlineFromKeyCommand) + ":"),
            input: "\r",
            modifierFlags: UIKit.UIKeyModifierFlags.Shift,
            propertyList: null)
    ];

    [Foundation.Export(nameof(InsertNewlineFromKeyCommand) + ":")]
    public void InsertNewlineFromKeyCommand(UIKit.UIKeyCommand command) => NewlineRequested?.Invoke();
}

#endif
