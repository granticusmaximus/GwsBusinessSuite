namespace GwsBusinessSuite.App;

// An Editor whose Return key sends instead of inserting a newline, with Shift+Return keeping the
// newline - the convention every chat client uses, and the thing a multi-line Editor does not do
// on its own.
//
// It is its own control rather than a behaviour attached to Editor because the Apple
// implementation needs a custom platform view (see ChatEditorHandler): UIKit only lets a
// UIResponder declare key commands by overriding a virtual property, which requires a subclass -
// there is no API to attach one to an existing UITextView. Making it a distinct control also
// keeps every other Editor in the app on stock behaviour.
public sealed class ChatEditor : Editor
{
    // Raised instead of inserting a newline when Return is pressed with no modifier.
    public event EventHandler? SendRequested;

    public void RaiseSendRequested() => SendRequested?.Invoke(this, EventArgs.Empty);
}
