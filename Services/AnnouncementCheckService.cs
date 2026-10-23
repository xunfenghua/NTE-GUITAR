using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Services;

public sealed class AnnouncementCheckService
{
    private const string AnnouncementEndpoint = "http://81.70.228.51:2888/api/announcement/current";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AppConfig _config;
    private readonly Action _saveConfig;

    public AnnouncementCheckService(AppConfig config, Action saveConfig)
    {
        _config = config;
        _saveConfig = saveConfig;
    }

    public async Task<object?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(AnnouncementEndpoint, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            return new { status = "unavailable" };

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var response = await client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return new { status = "unavailable" };

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!json.RootElement.TryGetProperty("announcement", out var announcementElement) ||
                announcementElement.ValueKind == JsonValueKind.Null)
                return new { status = "latest" };

            var announcement = announcementElement.Deserialize<AnnouncementDto>(JsonOptions);
            if (announcement is null || !announcement.Enabled || !IsValid(announcement))
                return new { status = "unavailable" };

            string fingerprint = CalculateFingerprint(announcement);
            if (string.Equals(fingerprint, _config.LastSeenAnnouncementFingerprint, StringComparison.Ordinal))
                return new { status = "latest" };

            return new { status = "new", fingerprint, announcement };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new { status = "unavailable" };
        }
        catch (HttpRequestException)
        {
            return new { status = "unavailable" };
        }
        catch (JsonException)
        {
            return new { status = "unavailable" };
        }
        catch (TaskCanceledException)
        {
            return new { status = "unavailable" };
        }
    }

    public bool MarkSeen(string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint) || fingerprint.Length != 64 ||
            fingerprint.Any(c => !Uri.IsHexDigit(c)))
            return false;

        _config.LastSeenAnnouncementFingerprint = fingerprint.ToLowerInvariant();
        _saveConfig();
        return true;
    }

    private static bool IsValid(AnnouncementDto announcement)
    {
        if (string.IsNullOrWhiteSpace(announcement.Id) || announcement.Id.Length > 100 ||
            string.IsNullOrWhiteSpace(announcement.Version) || announcement.Version.Length > 50 ||
            string.IsNullOrWhiteSpace(announcement.Title) || announcement.Title.Length > 160 ||
            string.IsNullOrWhiteSpace(announcement.Content) || announcement.Content.Length > 12000 ||
            (announcement.DownloadUrl?.Length ?? 0) > 2048)
            return false;

        if (string.IsNullOrWhiteSpace(announcement.DownloadUrl)) return true;
        return Uri.TryCreate(announcement.DownloadUrl, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static string CalculateFingerprint(AnnouncementDto announcement)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            announcement.Id,
            announcement.Version,
            announcement.Title,
            announcement.Content,
            DownloadUrl = announcement.DownloadUrl ?? ""
        }, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private sealed record AnnouncementDto(
        bool Enabled,
        string Id,
        string Version,
        string Title,
        string Content,
        string? DownloadUrl,
        DateTimeOffset? PublishedAt);
}
