using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace MidiAutoPlayer.Bridge;

public sealed class WebEventPublisher
{
    private CoreWebView2? _coreWebView2;
    private readonly SynchronizationContext? _syncContext;

    public WebEventPublisher(SynchronizationContext? syncContext = null)
    {
        _syncContext = syncContext ?? SynchronizationContext.Current;
    }

    public void Attach(CoreWebView2 core)
    {
        _coreWebView2 = core;
    }

    public void Publish(string eventName, object? data)
    {
        if (_coreWebView2 == null) return;

        var webEvent = new WebEvent(eventName, data);
        string json = JsonSerializer.Serialize(webEvent, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        if (_syncContext != null)
        {
            _syncContext.Post(_ =>
            {
                try
                {
                    _coreWebView2?.PostWebMessageAsJson(json);
                }
                catch { }
            }, null);
        }
        else
        {
            try
            {
                _coreWebView2.PostWebMessageAsJson(json);
            }
            catch { }
        }
    }
}
