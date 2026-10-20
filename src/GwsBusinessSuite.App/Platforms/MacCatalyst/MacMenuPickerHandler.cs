using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using UIKit;

namespace GwsBusinessSuite.App;

// Every Picker in the Mac app, drawn as a native pop-up button with a menu. MAUI's own Mac
// Catalyst picker opens a UIAlertController holding a UIPickerView, and UIKit throws
// ("unsupported non-Mac idiom behavior") the moment that wheel is added to a window, because
// this app runs in the Mac idiom (UIDeviceFamily 6, Info.plist). Any picker click crashed the app
// (crash report 20-10-26: UIPickerView _didMoveFromWindow under an alert presentation).
// The menu is built each time it opens, so it always lists the picker's current items; choosing
// one sets SelectedIndex exactly as the MAUI picker did, so pages need no changes.
public sealed class MacMenuPickerHandler : ViewHandler<IPicker, UIButton>
{
    public static readonly IPropertyMapper<IPicker, MacMenuPickerHandler> PickerMapper =
        new PropertyMapper<IPicker, MacMenuPickerHandler>(ViewMapper)
        {
            [nameof(IPicker.Title)] = (handler, _) => handler.UpdateButton(),
            [nameof(IPicker.SelectedIndex)] = (handler, _) => handler.UpdateButton(),
            [nameof(IPicker.Items)] = (handler, _) => handler.UpdateButton(),
            [nameof(ITextStyle.TextColor)] = (handler, _) => handler.UpdateButton(),
        };

    public static readonly CommandMapper<IPicker, MacMenuPickerHandler> PickerCommandMapper =
        new(ViewCommandMapper)
        {
            ["Reload"] = (handler, _, _) => handler.UpdateButton(),
        };

    public MacMenuPickerHandler() : base(PickerMapper, PickerCommandMapper)
    {
    }

    protected override UIButton CreatePlatformView()
    {
        var button = new UIButton(UIButtonType.System)
        {
            ShowsMenuAsPrimaryAction = true,
            Configuration = UIButtonConfiguration.BorderedButtonConfiguration
        };
        button.Menu = UIMenu.Create([UIDeferredMenuElement.CreateUncached(completion => completion(BuildActions()))]);
        return button;
    }

    private UIMenuElement[] BuildActions()
    {
        if (VirtualView is not { } picker) return [];
        var actions = new UIMenuElement[picker.GetCount()];
        for (var i = 0; i < actions.Length; i++)
        {
            var index = i;
            var action = UIAction.Create(picker.GetItem(i), null, null, _ =>
            {
                if (VirtualView is { } current && index < current.GetCount()) current.SelectedIndex = index;
            });
            action.State = index == picker.SelectedIndex ? UIMenuElementState.On : UIMenuElementState.Off;
            actions[i] = action;
        }
        return actions;
    }

    // The button shows the chosen item, or the picker's Title (its placeholder) when none is chosen.
    private void UpdateButton()
    {
        if (VirtualView is not { } picker || PlatformView?.Configuration is not { } configuration) return;
        var index = picker.SelectedIndex;
        configuration.Title = index >= 0 && index < picker.GetCount() ? picker.GetItem(index) : picker.Title ?? string.Empty;
        configuration.Indicator = UIButtonConfigurationIndicator.Popup;
        if (picker.TextColor is { } color) configuration.BaseForegroundColor = color.ToPlatform();
        PlatformView.Configuration = configuration;
    }
}
