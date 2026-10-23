using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Nte.GuitarChart.WinForms;

public sealed class BridgeController
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly WebView2 _webView;
    private readonly Func<string, JsonElement, Task> _dispatchAsync;
    private readonly SynchronizationContext? _uiContext;

    public BridgeController(WebView2 webView, Func<string, JsonElement, Task> dispatchAsync)
    {
        _webView = webView;
        _dispatchAsync = dispatchAsync;
        _uiContext = SynchronizationContext.Current;
    }

    public async Task InitializeAsync(string webRoot)
    {
        await _webView.EnsureCoreWebView2Async();
        _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        // Map the application asset root so the overlay can reuse shared web assets.
        var overlayDirectory = new DirectoryInfo(Path.GetFullPath(webRoot));
        var assetRoot = overlayDirectory.Parent?.Parent?.FullName ?? overlayDirectory.FullName;
        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "nte.local", assetRoot, CoreWebView2HostResourceAccessKind.Allow);
        _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        _webView.Source = new Uri("https://nte.local/Web/SemiAutoOverlay/index.html");
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString() ?? "";
            var payload = root.TryGetProperty("payload", out var p) ? p.Clone() : default;
            await _dispatchAsync(type, payload);
        }
        catch (Exception ex)
        {
            Publish(new {
                type = "app.toast",
                payload = new { level = "error", message = $"消息处理失败：{ex.Message}" }
            });
        }
    }

    public void Publish<T>(T message)
    {
        var json = JsonSerializer.Serialize(message, JsonOptions);
        void Send()
        {
            try
            {
                _webView.CoreWebView2?.PostWebMessageAsJson(json);
            }
            catch (InvalidOperationException)
            {
                // The WebView may still be navigating; web.ready will replay current state.
            }
        }

        if (_uiContext != null && SynchronizationContext.Current != _uiContext)
        {
            _uiContext.Post(_ => Send(), null);
        }
        else if (_webView.InvokeRequired)
        {
            _webView.BeginInvoke((Action)Send);
        }
        else
        {
            Send();
        }
    }
}
