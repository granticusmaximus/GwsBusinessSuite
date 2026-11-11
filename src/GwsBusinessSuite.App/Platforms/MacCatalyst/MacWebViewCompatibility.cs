using System.Runtime.CompilerServices;
using Foundation;
using WebKit;

namespace GwsBusinessSuite.App;

internal static class MacWebViewCompatibility
{
    private static readonly ConditionalWeakTable<WKWebView, object> ConfiguredViews = new();
    internal const string Script = """
        window.__gwsMacCatalystWebView = true;
        (function () {
            function installNativeNavigation() {
                const container = document.getElementById('tg-viewport');
                if (!container || container.dataset.gwsNativeAppNavigation === 'true') return;
                container.dataset.gwsNativeAppNavigation = 'true';
                container.style.touchAction = 'none';
                let pointer = null;
                function post(operation, args) {
                    const handlers = window.webkit && window.webkit.messageHandlers;
                    const handler = handlers && handlers.gwsNativeOverwatch;
                    if (handler) handler.postMessage({ operation: operation, arguments: args });
                }
                container.addEventListener('pointerdown', function (event) {
                    if (event.button !== 0) return;
                    pointer = { id: event.pointerId, x: event.clientX, y: event.clientY };
                    container.setPointerCapture(event.pointerId);
                });
                container.addEventListener('pointermove', function (event) {
                    if (!pointer || pointer.id !== event.pointerId) return;
                    const bounds = container.getBoundingClientRect();
                    if (bounds.width <= 0 || bounds.height <= 0) return;
                    const deltaX = (event.clientX - pointer.x) / bounds.width;
                    const deltaY = (event.clientY - pointer.y) / bounds.height;
                    if (Math.abs(deltaX) + Math.abs(deltaY) < 0.001) return;
                    pointer.x = event.clientX;
                    pointer.y = event.clientY;
                    post('pan', [deltaX, deltaY]);
                });
                function finish(event) {
                    if (pointer && pointer.id === event.pointerId) pointer = null;
                }
                container.addEventListener('pointerup', finish);
                container.addEventListener('pointercancel', finish);
                container.addEventListener('wheel', function (event) {
                    event.preventDefault();
                    post('zoom', ['tg-viewport', event.deltaY < 0]);
                }, { passive: false });
            }
            new MutationObserver(installNativeNavigation).observe(document.documentElement, {
                childList: true,
                subtree: true
            });
            document.addEventListener('DOMContentLoaded', installNativeNavigation);
            installNativeNavigation();
        })();
        """;

    public static void Configure(WKWebView webView)
    {
        if (ConfiguredViews.TryGetValue(webView, out _)) return;
        ConfiguredViews.Add(webView, new object());

        MacNativeOverwatchBridge.Attach(webView);
        webView.Configuration.UserContentController.AddUserScript(
            new WKUserScript(
                new NSString(Script),
                WKUserScriptInjectionTime.AtDocumentStart,
                false));
    }
}
