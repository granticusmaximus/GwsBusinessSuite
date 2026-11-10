using Foundation;
using Microsoft.Maui.ApplicationModel;
using SafariServices;
using UIKit;

namespace GwsBusinessSuite.App;

internal static class OverwatchSafariPresenter
{
    public static void Present(Uri url)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var presenter = Platform.GetCurrentUIViewController();
            if (presenter is null) return;

            while (presenter.PresentedViewController is not null)
            {
                presenter = presenter.PresentedViewController;
            }

            presenter.PresentViewController(
                new SFSafariViewController(new NSUrl(url.AbsoluteUri)),
                animated: true,
                completionHandler: null);
        });
    }
}
