using Foundation;
using UIKit;

namespace GwsBusinessSuite.App;

[Register("SceneDelegate")]
public class SceneDelegate : MauiUISceneDelegate
{
    public override void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        base.WillConnect(scene, session, connectionOptions);
        if (scene is UIWindowScene windowScene)
        {
            MacWindowToolbar.Attach(windowScene);
        }
    }
}
