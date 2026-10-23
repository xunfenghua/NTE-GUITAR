using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Services;

public sealed class RecommendedScoreService : IDisposable
{
    public const string DefaultServerBaseUrl = "http://81.70.228.51:2888";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly Regex SafeId = new("^[A-Za-z0-9_-]{1,80}$", RegexOptions.Compiled);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly Uri _serverBase;
    private readonly string _cacheDirectory;
    private readonly string _catalogCachePath;

    public RecommendedScoreService(string? cacheDirectory = null, HttpClient? httpClient = null, string? serverBaseUrl = null)
    {
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        string environmentUrl = Environment.GetEnvironmentVariable("NTE_GUITAR_RECOMMENDED_SERVER_URL") ?? "";
        string baseUrl = string.IsNullOrWhiteSpace(serverBaseUrl)
            ? string.IsNullOrWhiteSpace(environmentUrl) ? DefaultServerBaseUrl : environmentUrl
            : serverBaseUrl;
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var serverBase) ||
            (serverBase.Scheme != Uri.UriSchemeHttp && serverBase.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("推荐谱服务地址必须是 HTTP/HTTPS 地址。", nameof(serverBaseUrl));
        _serverBase = serverBase;

        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NTE-GUITAR", "recommended");
        _catalogCachePath = Path.Combine(_cacheDirectory, "catalog.json");
    }

    public async Task<object> GetCatalogAsync(string query = "", int? level = null, CancellationToken cancellationToken = default, string? instrument = null)
    {
        if (level.HasValue && !RecommendedScore.IsValidDifficultyLevel(level.Value))
            throw new ArgumentOutOfRangeException(nameof(level), "难度等级必须在 1 至 16 之间。");

        bool offline = false;
        List<RecommendedScore> scores;
        try
        {
            using var response = await _httpClient.GetAsync(new Uri(_serverBase, "api/recommendations"), cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var envelope = await JsonSerializer.DeserializeAsync<CatalogEnvelope>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("推荐谱目录响应为空。");
            scores = ValidateCatalog(envelope.Items);
            SaveCatalogCache(scores);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException or InvalidDataException)
        {
            scores = LoadCatalogCache();
            offline = true;
            if (scores.Count == 0)
                return new { status = "unavailable", offline, items = Array.Empty<object>() };
        }

        if (!string.IsNullOrWhiteSpace(query))
            scores = scores.Where(s => s.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                       s.Artist.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (level.HasValue) scores = scores.Where(s => s.DifficultyLevel == level.Value).ToList();
        if (!string.IsNullOrWhiteSpace(instrument)) scores = scores.Where(s => string.Equals(s.Instrument, instrument, StringComparison.OrdinalIgnoreCase)).ToList();

        var items = scores
            .OrderBy(s => s.DifficultyLevel)
            .ThenBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(s => new
            {
                id = s.Id,
                title = s.Title,
                artist = s.Artist,
                uploaderName = s.UploaderName,
                instrument = s.Instrument,
                difficultyLevel = s.DifficultyLevel,
                difficultyLabel = s.DifficultyLabel,
                displayDifficulty = s.DisplayDifficulty,
                sha256 = s.Sha256,
                sizeBytes = s.SizeBytes,
                updatedAt = s.UpdatedAt,
                isCached = IsCached(s),
                midiUrl = s.MidiUrl
            }).ToList();

        return new { status = "ok", offline, items };
    }

    public async Task<RecommendedScoreDownload> DownloadAsync(string id, CancellationToken cancellationToken = default)
    {
        string safeId = ValidateId(id);
        var scores = LoadCatalogCache();
        var score = scores.FirstOrDefault(s => s.Id == safeId)
            ?? await FetchScoreAsync(safeId, cancellationToken);

        string targetPath = GetCachedPath(score);
        if (File.Exists(targetPath) && await FileMatchesHashAsync(targetPath, score.Sha256, cancellationToken))
            return new RecommendedScoreDownload(score, targetPath, true);

        Uri downloadUri = ResolveDownloadUri(score);
        using var response = await _httpClient.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && (length < 1 || length > 32 * 1024 * 1024))
            throw new InvalidDataException("推荐谱 MIDI 文件大小超出允许范围。");

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        string tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await input.CopyToAsync(output, cancellationToken);
            }

            var fileInfo = new FileInfo(tempPath);
            if (fileInfo.Length < 1 || fileInfo.Length > 32 * 1024 * 1024)
                throw new InvalidDataException("推荐谱 MIDI 文件大小超出允许范围。");
            if (!await FileMatchesHashAsync(tempPath, score.Sha256, cancellationToken))
                throw new InvalidDataException("推荐谱文件校验失败，文件可能已损坏或服务器版本不一致。");

            File.Move(tempPath, targetPath, overwrite: true);
            return new RecommendedScoreDownload(score, targetPath, false);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    public async Task<RecommendedScoreSubmission> SubmitAsync(string title, string uploaderName, string instrument, int level, string? label, string filePath, CancellationToken cancellationToken = default)
    {
        title = (title ?? "").Trim();
        uploaderName = (uploaderName ?? "").Trim();
        if (title.Length is < 1 or > 160) throw new ArgumentException("曲名长度需为 1 至 160 个字符。", nameof(title));
        if (uploaderName.Length is < 1 or > 80) throw new ArgumentException("上传者名称长度需为 1 至 80 个字符。", nameof(uploaderName));
        if (instrument is not ("keyboard" or "piano" or "guitar")) throw new ArgumentException("乐器类型无效。", nameof(instrument));
        if (!RecommendedScore.IsValidDifficultyLevel(level)) throw new ArgumentOutOfRangeException(nameof(level), "难度等级必须在 1 至 16 之间。");
        if ((label?.Trim().Length ?? 0) > 40) throw new ArgumentException("难度文本最多 40 个字符。", nameof(label));
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) throw new FileNotFoundException("请选择 MIDI 文件后再投稿。", filePath);
        string extension = Path.GetExtension(filePath);
        if (!extension.Equals(".mid", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".midi", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择 .mid 或 .midi 文件。");
        var info = new FileInfo(filePath);
        if (info.Length is < 14 or > 20 * 1024 * 1024) throw new InvalidDataException("MIDI 文件大小需在 14 字节至 20 MiB 之间。");
        await using (var check = File.OpenRead(filePath))
        {
            byte[] header = new byte[4];
            if (await check.ReadAsync(header, cancellationToken) != 4 || !header.SequenceEqual("MThd"u8.ToArray()))
                throw new InvalidDataException("所选文件不是有效的 MIDI 文件。");
        }
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(title), "title");
        form.Add(new StringContent(uploaderName), "uploaderName");
        form.Add(new StringContent(instrument), "instrument");
        form.Add(new StringContent(level.ToString(System.Globalization.CultureInfo.InvariantCulture)), "difficultyLevel");
        if (!string.IsNullOrWhiteSpace(label)) form.Add(new StringContent(label.Trim()), "difficultyLabel");
        await using var file = File.OpenRead(filePath);
        using var content = new StreamContent(file);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/midi");
        form.Add(content, "midi", Path.GetFileName(filePath));
        using var response = await _httpClient.PostAsync(new Uri(_serverBase, "api/recommendations/submissions"), form, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string detail = await response.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                using var errorJson = JsonDocument.Parse(detail);
                if (errorJson.RootElement.TryGetProperty("error", out var error)) detail = error.GetString() ?? detail;
            }
            catch { }
            if (detail.Length > 500) detail = detail[..500];
            throw new HttpRequestException($"投稿失败（HTTP {(int)response.StatusCode}）：{detail}", null, response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<RecommendedScoreSubmission>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("投稿服务未返回审核编号。");
    }

    public string GetCachedMidiPath(string id, string sha256)
    {
        string safeId = ValidateId(id);
        ValidateSha256(sha256);
        return Path.Combine(_cacheDirectory, safeId, sha256.ToLowerInvariant() + ".mid");
    }

    private async Task<RecommendedScore> FetchScoreAsync(string id, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(new Uri(_serverBase, $"api/recommendations/{Uri.EscapeDataString(id)}"), cancellationToken);
        response.EnsureSuccessStatusCode();
        var score = await response.Content.ReadFromJsonAsync<RecommendedScore>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("未找到推荐谱曲目。");
        return ValidateScore(score);
    }

    private Uri ResolveDownloadUri(RecommendedScore score)
    {
        if (string.IsNullOrWhiteSpace(score.MidiUrl))
            return new Uri(_serverBase, $"api/recommendations/{Uri.EscapeDataString(score.Id)}/midi");
        if (!Uri.TryCreate(_serverBase, score.MidiUrl, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.Equals(uri.Host, _serverBase.Host, StringComparison.OrdinalIgnoreCase) || uri.Port != _serverBase.Port)
            throw new InvalidDataException("推荐谱目录包含无效的 MIDI 下载地址。");
        return uri;
    }

    private List<RecommendedScore> ValidateCatalog(List<RecommendedScore>? scores)
    {
        if (scores is null || scores.Count > 5000) throw new InvalidDataException("推荐谱目录内容无效或条目过多。");
        var validated = scores.Select(ValidateScore).ToList();
        if (validated.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != validated.Count)
            throw new InvalidDataException("推荐谱目录中存在重复 ID。");
        return validated;
    }

    private static RecommendedScore ValidateScore(RecommendedScore score)
    {
        ValidateId(score.Id);
        if (string.IsNullOrWhiteSpace(score.Title) || score.Title.Length > 160)
            throw new InvalidDataException("推荐谱标题无效。");
        if (!RecommendedScore.IsValidDifficultyLevel(score.DifficultyLevel))
            throw new InvalidDataException("推荐谱难度等级必须在 1 至 16 之间。");
        if (score.Instrument is not ("keyboard" or "piano" or "guitar"))
            throw new InvalidDataException("推荐谱乐器类型无效。");
        if (score.SizeBytes is < 1 or > 32 * 1024 * 1024)
            throw new InvalidDataException("推荐谱 MIDI 文件大小无效。");
        ValidateSha256(score.Sha256);
        if ((score.DifficultyLabel?.Length ?? 0) > 50 || score.Artist.Length > 160 || score.UploaderName.Length > 80)
            throw new InvalidDataException("推荐谱标签或作者信息过长。");
        return score with
        {
            DifficultyLabel = string.IsNullOrWhiteSpace(score.DifficultyLabel) ? null : score.DifficultyLabel.Trim(),
            Artist = score.Artist.Trim()
        };
    }

    private bool IsCached(RecommendedScore score)
    {
        string path = GetCachedPath(score);
        return File.Exists(path);
    }

    private string GetCachedPath(RecommendedScore score) => GetCachedMidiPath(score.Id, score.Sha256);

    private static string ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !SafeId.IsMatch(id))
            throw new ArgumentException("推荐谱 ID 格式无效。", nameof(id));
        return id;
    }

    private static void ValidateSha256(string hash)
    {
        if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("推荐谱 SHA-256 格式无效。");
    }

    private static async Task<bool> FileMatchesHashAsync(string path, string expectedHash, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        byte[] actual = await SHA256.HashDataAsync(stream, cancellationToken);
        return string.Equals(Convert.ToHexString(actual), expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private void SaveCatalogCache(List<RecommendedScore> scores)
    {
        Directory.CreateDirectory(_cacheDirectory);
        string temp = _catalogCachePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new CatalogEnvelope(scores), JsonOptions));
        File.Move(temp, _catalogCachePath, overwrite: true);
    }

    private List<RecommendedScore> LoadCatalogCache()
    {
        try
        {
            if (!File.Exists(_catalogCachePath)) return new List<RecommendedScore>();
            var envelope = JsonSerializer.Deserialize<CatalogEnvelope>(File.ReadAllText(_catalogCachePath), JsonOptions);
            return envelope is null ? new List<RecommendedScore>() : ValidateCatalog(envelope.Items);
        }
        catch { return new List<RecommendedScore>(); }
    }

    private sealed record CatalogEnvelope(List<RecommendedScore>? Items);

    public void Dispose()
    {
        if (_ownsClient) _httpClient.Dispose();
    }
}

