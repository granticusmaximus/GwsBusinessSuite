namespace GwsBusinessSuite.App;

// Android and Windows expose a real key-down event carrying modifier state, so both the send and
// the newline case are decided in one place - no custom platform view is needed the way it is on
// UIKit (see ChatEditorHandler). Applied through the handler mapper, which is additive and
// leaves MAUI's own Editor behaviour intact.
public static class SendOnEnterBehavior
{
    public static void Configure()
    {
#if ANDROID
        Microsoft.Maui.Handlers.EditorHandler.Mapper.AppendToMapping(
            "GwsSendOnEnter",
            (handler, view) =>
            {
                if (view is not ChatEditor editor) return;
                handler.PlatformView.KeyPress += (_, args) =>
                {
                    if (args.KeyCode != Android.Views.Keycode.Enter
                        || args.Event?.Action != Android.Views.KeyEventActions.Down
                        || args.Event.IsShiftPressed)
                    {
                        // Not our case, or Shift is held - let the platform insert the newline.
                        args.Handled = false;
                        return;
                    }

                    editor.RaiseSendRequested();
                    args.Handled = true;
                };
            });
#elif WINDOWS
        Microsoft.Maui.Handlers.EditorHandler.Mapper.AppendToMapping(
            "GwsSendOnEnter",
            (handler, view) =>
            {
                if (view is not ChatEditor editor) return;
                handler.PlatformView.KeyDown += (_, args) =>
                {
                    if (args.Key != Windows.System.VirtualKey.Enter) return;

                    var shift = Microsoft.UI.Input.InputKeyboardSource
                        .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
                    if (shift.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;

                    editor.RaiseSendRequested();
                    args.Handled = true;
                };
            });
#endif
    }
}
