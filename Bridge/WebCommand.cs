using System.Text.Json.Serialization;

namespace MidiAutoPlayer.Bridge;

public sealed class WebRequest
{
    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("payload")]
    public System.Text.Json.JsonElement Payload { get; set; }
}

public sealed class WebResponse
{
    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("data")]
    public object? Data { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    public static WebResponse Ok(string? requestId, object? data)
        => new() { RequestId = requestId, Success = true, Data = data };

    public static WebResponse Fail(string? requestId, string error)
        => new() { RequestId = requestId, Success = false, Error = error };
}

public sealed class WebEvent
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public object? Data { get; set; }

    public WebEvent(string eventName, object? data)
    {
        Event = eventName;
        Data = data;
    }
}

public sealed record GuitarSimulationSnapshot(
    string Mode,
    string CurrentPage,
    string? CurrentChord,
    IReadOnlyList<GuitarNoteMarker> ActiveNotes,
    IReadOnlyList<GuitarNoteMarker> UpcomingNotes,
    string PlaybackState,
    string? DiagnosticMessage,
    long PositionMs
);

public sealed record GuitarNoteMarker(
    int MidiPitch,
    int GameKey,
    int? PhysicalString,
    int? Fret,
    long StartMs,
    long DurationMs,
    bool IsPageChangeRequired
);

public sealed record GuitarSimulationActionDto(
    string Mode,
    string CurrentPage,
    int CurrentSlot,
    string ActionType, // "page" | "slot" | "pluck" | "strumDown" | "strumUp" | "idle"
    int StringNo,
    int Fret,
    string? ChordName,
    object? GameKey,
    string Message,
    long TimestampMs,
    IReadOnlyList<GuitarNoteMarker>? ActiveNotes = null,
    string? PlaybackState = null
);

