using Foundation;
using Microsoft.Maui.Platform;
using UIKit;

namespace GwsBusinessSuite.App;

[Register("SceneDelegate")]
public class SceneDelegate : MauiUISceneDelegate
{
    public override void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        base.WillConnect(scene, session, connectionOptions);
        if (scene is UIWindowScene windowScene
            && Window.GetWindow() is Microsoft.Maui.Controls.Window { Page: AppShell })
        {
            MacWindowToolbar.Attach(windowScene);
        }
    }

    public override void OnActivated(UIScene scene)
    {
        base.OnActivated(scene);
        MacToolbarActions.ActiveSentinelWindow = Window.GetWindow() is Microsoft.Maui.Controls.Window
            { Page: MainPage { IsSentinelWindow: true } page } ? page : null;
    }

    public override void OnResignActivation(UIScene scene)
    {
        if (Window.GetWindow() is Microsoft.Maui.Controls.Window { Page: MainPage page }
            && ReferenceEquals(MacToolbarActions.ActiveSentinelWindow, page))
            MacToolbarActions.ActiveSentinelWindow = null;
        base.OnResignActivation(scene);
    }

    public override void DidDisconnect(UIScene scene)
    {
        if (Window.GetWindow() is Microsoft.Maui.Controls.Window { Page: MainPage page }
            && ReferenceEquals(MacToolbarActions.ActiveSentinelWindow, page))
            MacToolbarActions.ActiveSentinelWindow = null;
        base.DidDisconnect(scene);
    }
}
