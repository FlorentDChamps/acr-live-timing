using System.Windows;
using Microsoft.Web.WebView2.Wpf;

namespace ACRLiveTiming.UI
{
    /// <summary>Runs the page's own canvas exporter in an off-screen WebView2. The
    /// browser never receives a webhook; it returns only a PNG data URL to the IHM.</summary>
    public sealed class DiscordPngRenderer
    {
        public Task<byte[]> RenderStandingsAsync(string url)
            => RenderAsync(url, "standingsPng()", "standings");

        public Task<byte[]> RenderRallyAsync(string url, int group)
            => RenderAsync(url, $"rallyPng({group})", "rally");

        async Task<byte[]> RenderAsync(string url, string exportCall, string label)
        {
            var web = new WebView2();
            var window = new Window
            {
                Width = 1200,
                Height = 900,
                Left = -32000,
                Top = -32000,
                Opacity = 0.01,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                Content = web
            };
            try
            {
                window.Show(); // WebView2 needs a real HWND even though it stays off-screen.
                await web.EnsureCoreWebView2Async();
                var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                web.CoreWebView2.NavigationCompleted += (_, e) =>
                {
                    if (e.IsSuccess) loaded.TrySetResult();
                    else loaded.TrySetException(new InvalidOperationException($"Could not load Discord {label} page (0x{e.WebErrorStatus:X})."));
                };
                web.CoreWebView2.Navigate(url);
                await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
                // ExecuteScriptAsync serializes a JavaScript Promise as an object
                // instead of awaiting it. Return the completed canvas result over
                // WebView2's message channel, which also keeps the async error intact.
                var pngReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                web.CoreWebView2.WebMessageReceived += (_, e) =>
                {
                    var message = e.TryGetWebMessageAsString();
                    const string ok = "acr-discord-png:";
                    const string fail = "acr-discord-png-error:";
                    if (message.StartsWith(ok, StringComparison.Ordinal)) pngReady.TrySetResult(message[ok.Length..]);
                    else if (message.StartsWith(fail, StringComparison.Ordinal))
                        pngReady.TrySetException(new InvalidOperationException(message[fail.Length..]));
                };
                await web.CoreWebView2.ExecuteScriptAsync(
                    $"window.acrLiveTimingExport.{exportCall}" +
                    ".then(value => chrome.webview.postMessage('acr-discord-png:' + value))" +
                    ".catch(error => chrome.webview.postMessage('acr-discord-png-error:' + String(error)));"
                );
                var dataUrl = await pngReady.Task.WaitAsync(TimeSpan.FromSeconds(30));
                const string prefix = "data:image/png;base64,";
                if (!dataUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"The Discord {label} page returned an invalid PNG.");
                return Convert.FromBase64String(dataUrl[prefix.Length..]);
            }
            finally
            {
                web.Dispose();
                window.Close();
            }
        }
    }
}
