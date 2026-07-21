#if ANDROID
using Android.Webkit;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;

namespace GwsBusinessSuite.App;

internal sealed class TrustedMediaWebChromeClient(IWebViewHandler handler) : MauiWebChromeClient(handler)
{
    public override void OnPermissionRequest(PermissionRequest? request)
    {
        if (request is null)
        {
            return;
        }

        var resources = request.GetResources() ?? [];
        if (!IsTrustedMediaRequest(request, resources))
        {
            request.Deny();
            return;
        }

        _ = GrantMediaPermissionsAsync(request, resources);
    }

    private static bool IsTrustedMediaRequest(PermissionRequest request, IReadOnlyCollection<string> resources)
    {
        if (!Uri.TryCreate(request.Origin?.ToString(), UriKind.Absolute, out var origin)
            || !AppEndpoints.IsTrusted(origin)
            || resources.Count == 0)
        {
            return false;
        }

        return resources.All(resource =>
            resource is PermissionRequest.ResourceVideoCapture or PermissionRequest.ResourceAudioCapture);
    }

    private static async Task GrantMediaPermissionsAsync(
        PermissionRequest request,
        IReadOnlyCollection<string> resources)
    {
        var granted = true;

        try
        {
            if (resources.Contains(PermissionRequest.ResourceVideoCapture))
            {
                granted = await Permissions.RequestAsync<Permissions.Camera>() == PermissionStatus.Granted;
            }

            if (granted && resources.Contains(PermissionRequest.ResourceAudioCapture))
            {
                granted = await Permissions.RequestAsync<Permissions.Microphone>() == PermissionStatus.Granted;
            }
        }
        catch (PermissionException)
        {
            granted = false;
        }

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (granted)
            {
                request.Grant(resources.ToArray());
            }
            else
            {
                request.Deny();
            }
        });
    }
}
#endif
