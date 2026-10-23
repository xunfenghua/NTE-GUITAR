using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using NteGuitar.AnnouncementServer;

const long MaxMidiBytes = 20 * 1024 * 1024;
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://0.0.0.0:2888");
int submissionLimitPerMinute = int.TryParse(Environment.GetEnvironmentVariable("NTE_GUITAR_SUBMISSION_LIMIT_PER_MINUTE"), out int configuredSubmissionLimit)
    ? Math.Clamp(configuredSubmissionLimit, 1, 500)
    : 30;
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    string trustedProxyIps = Environment.GetEnvironmentVariable("NTE_GUITAR_TRUSTED_PROXY_IPS") ?? "";
    foreach (string value in trustedProxyIps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        if (System.Net.IPAddress.TryParse(value, out var ip)) options.KnownProxies.Add(ip);
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    AddLimit(options, "announcement-read", 20);
    AddLimit(options, "recommendation-read", 60);
    AddLimit(options, "recommendation-midi", 30);
    AddLimit(options, "recommendation-admin", 10);
    AddLimit(options, "client-presence-write", 30);
});
builder.Services.AddSingleton<ClientPresenceStore>();
var submissionLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    RateLimitPartition.GetSlidingWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new SlidingWindowRateLimiterOptions
    {
        PermitLimit = submissionLimitPerMinute,
        Window = TimeSpan.FromMinutes(1),
        SegmentsPerWindow = 6,
        QueueLimit = 0,
        AutoReplenishment = true
    }));
var app = builder.Build();
app.UseForwardedHeaders();
app.UseRateLimiter();
app.Lifetime.ApplicationStopped.Register(submissionLimiter.Dispose);
var dataDir = Path.GetFullPath(Environment.GetEnvironmentVariable("NTE_GUITAR_DATA_DIR") ?? Path.Combine(AppContext.BaseDirectory, "data"));
var midiDir = Path.Combine(dataDir, "midi");
var catalogPath = Path.Combine(dataDir, "recommendations.json");
var submissionDir = Path.Combine(dataDir, "submissions");
var submissionMidiDir = Path.Combine(submissionDir, "midi");
var pendingPath = Path.Combine(submissionDir, "pending.json");
Directory.CreateDirectory(midiDir);
Directory.CreateDirectory(submissionMidiDir);
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true, WriteIndented = true };
var gate = new SemaphoreSlim(1, 1);

app.MapPost("/api/client-presence/heartbeat", (ClientPresenceHeartbeatRequest? request, ClientPresenceStore presence) =>
{
    if (request is null || request.SessionId == Guid.Empty)
        return Results.BadRequest(new { error = "sessionId must be a non-empty GUID." });

    var version = request.Version?.Trim();
    if (string.IsNullOrWhiteSpace(version) || version.Length > 50 || version.Any(char.IsControl))
        return Results.BadRequest(new { error = "version must contain 1 to 50 visible characters." });

    presence.Heartbeat(request.SessionId, version);
    return Results.NoContent();
})
    .RequireRateLimiting("client-presence-write")
    .WithMetadata(new RequestSizeLimitAttribute(1024));

app.MapGet("/api/admin/client-presence", (HttpRequest request, ClientPresenceStore presence) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    return Results.Ok(presence.Snapshot());
}).RequireRateLimiting("recommendation-admin");

app.MapGet("/api/announcement/current", async (CancellationToken ct) =>
{
    string filePath = Path.Combine(AppContext.BaseDirectory, "announcement.json");
    if (!File.Exists(filePath)) return Results.Ok(new CurrentAnnouncementResponse(null));
    Announcement? item;
    try { await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); item = await JsonSerializer.DeserializeAsync<Announcement>(stream, jsonOptions, ct); }
    catch (JsonException) { return Results.Problem("announcement.json is not valid JSON.", statusCode: 503); }
    catch (IOException) { return Results.Problem("announcement.json could not be read.", statusCode: 503); }
    if (item is null) return Results.Problem("announcement.json must contain a JSON object.", statusCode: 503);
    if (!item.Enabled) return Results.Ok(new CurrentAnnouncementResponse(null));
    var error = item.Validate();
    return error is not null ? Results.Problem(error, statusCode: 503) : Results.Ok(new CurrentAnnouncementResponse(item));
}).RequireRateLimiting("announcement-read");

app.MapGet("/api/recommendations", async (HttpRequest request, int? level, string? query, CancellationToken ct) =>
{
    if (level is < 1 or > 16) return Results.BadRequest(new { error = "level must be between 1 and 16." });
    var all = await ReadCatalog(ct);
    IEnumerable<Recommendation> items = all;
    if (level is not null) items = items.Where(x => x.DifficultyLevel == level.Value);
    var term = (query ?? request.Query["q"].ToString()).Trim();
    if (term.Length > 100) return Results.BadRequest(new { error = "query is too long." });
    if (term.Length > 0) items = items.Where(x => x.Title.Contains(term, StringComparison.OrdinalIgnoreCase) || x.Artist.Contains(term, StringComparison.OrdinalIgnoreCase));
    var result = items.OrderBy(x => x.DifficultyLevel).ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
        .Select(x => new { x.Id, x.Title, x.Artist, x.UploaderName, x.Instrument, x.DifficultyLevel, x.DifficultyLabel, x.Sha256, x.SizeBytes, x.UpdatedAt, midiUrl = $"/api/recommendations/{x.Id}/midi" });
    return Results.Ok(new { items = result });
}).RequireRateLimiting("recommendation-read");

app.MapPost("/api/recommendations/submissions", async (HttpRequest request, CancellationToken ct) =>
{
    if (!request.HasFormContentType) return Results.BadRequest(new { error = "multipart/form-data required." });
    var form = await request.ReadFormAsync(ct);
    var title = form["title"].ToString().Trim();
    var uploaderName = form["uploaderName"].ToString().Trim();
    var instrument = form["instrument"].ToString().Trim().ToLowerInvariant();
    var label = form["difficultyLabel"].ToString().Trim();
    if (title.Length is < 1 or > 160) return Results.BadRequest(new { error = "title is required (max 160)." });
    if (uploaderName.Length is < 1 or > 80) return Results.BadRequest(new { error = "uploaderName is required (max 80)." });
    if (instrument is not ("keyboard" or "guitar" or "piano")) return Results.BadRequest(new { error = "instrument must be keyboard, piano, or guitar." });
    if (!int.TryParse(form["difficultyLevel"], out var difficulty) || difficulty is < 1 or > 16) return Results.BadRequest(new { error = "difficultyLevel must be between 1 and 16." });
    if (label.Length > 40) return Results.BadRequest(new { error = "difficultyLabel max 40 characters." });
    var file = form.Files.GetFile("midi");
    if (file is null || file.Length < 1 || file.Length > MaxMidiBytes) return Results.BadRequest(new { error = "MIDI file is required and must be at most 20 MiB." });
    var extension = Path.GetExtension(file.FileName);
    if (!string.Equals(extension, ".mid", StringComparison.OrdinalIgnoreCase) && !string.Equals(extension, ".midi", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new { error = "Upload a .mid or .midi file." });
    await gate.WaitAsync(ct);
    try
    {
        var id = Guid.NewGuid().ToString("N");
        var stagingPath = Path.Combine(submissionMidiDir, id + ".upload.tmp");
        await using (var output = new FileStream(stagingPath, FileMode.Create, FileAccess.Write, FileShare.None)) await file.CopyToAsync(output, ct);
        var bytes = await File.ReadAllBytesAsync(stagingPath, ct);
        if (bytes.Length < 4 || bytes[0] != (byte)'M' || bytes[1] != (byte)'T' || bytes[2] != (byte)'h' || bytes[3] != (byte)'d')
        {
            File.Delete(stagingPath);
            return Results.BadRequest(new { error = "The uploaded file does not have a valid MIDI header." });
        }
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var catalog = await ReadCatalogUnlocked(ct);
        var pending = await ReadPendingUnlocked(ct);
        if (catalog.Any(x => x.Sha256 == hash) || pending.Any(x => x.Sha256 == hash))
        {
            File.Delete(stagingPath);
            return Results.Conflict(new { error = "An identical MIDI file is already published or awaiting review." });
        }
        using var submissionLease = await submissionLimiter.AcquireAsync(request.HttpContext, 1, ct);
        if (!submissionLease.IsAcquired)
        {
            File.Delete(stagingPath);
            return Results.Json(new { error = "Too many valid submissions from this IP address. Please try again later." }, statusCode: StatusCodes.Status429TooManyRequests);
        }
        var storageName = $"{id}.{hash}.mid";
        File.Move(stagingPath, Path.Combine(submissionMidiDir, storageName), true);
        var submission = new PendingSubmission(id, title, uploaderName, instrument, difficulty, label, hash, bytes.LongLength, DateTimeOffset.UtcNow, storageName);
        try { await WritePending(pending.Append(submission).ToList(), ct); }
        catch
        {
            var newPath = Path.Combine(submissionMidiDir, storageName);
            if (File.Exists(newPath)) File.Delete(newPath);
            throw;
        }
        return Results.Accepted($"/api/recommendations/submissions/{id}", new { id, status = "pending" });
    }
    catch (IOException) { return Results.Problem("Unable to store the submission.", statusCode: 500); }
    finally { gate.Release(); }
}).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxMidiBytes + 1024 * 1024));

app.MapGet("/api/recommendations/{id}/midi", async (string id, HttpContext context, CancellationToken ct) =>
{
    if (!IsSafeId(id)) return Results.NotFound();
    var item = (await ReadCatalog(ct)).FirstOrDefault(x => x.Id == id);
    if (item is null) return Results.NotFound();
    var filePath = Path.Combine(midiDir, item.FileName);
    if (!File.Exists(filePath)) return Results.NotFound();
    var etag = $"\"{item.Sha256}\"";
    context.Response.Headers.ETag = etag;
    context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
    if (context.Request.Headers.IfNoneMatch.Any(x => x == etag || x == "*")) return Results.StatusCode(304);
    return Results.File(filePath, "audio/midi", fileDownloadName: $"{SafeName(item.Title)}.mid", enableRangeProcessing: true, lastModified: item.UpdatedAt);
}).RequireRateLimiting("recommendation-midi");

app.MapGet("/admin/recommendations", async (CancellationToken ct) => Results.Content(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "admin.html"), ct), "text/html; charset=utf-8"));
app.MapGet("/api/admin/submissions", async (HttpRequest request, CancellationToken ct) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    var pending = await ReadPending(ct);
    var rows = pending.OrderBy(x => x.SubmittedAt).Select(x => new { x.Id, x.Title, x.UploaderName, x.Instrument, x.DifficultyLevel, x.DifficultyLabel, x.Sha256, x.SizeBytes, x.SubmittedAt, midiUrl = $"/api/admin/submissions/{x.Id}/midi" });
    return Results.Ok(new { items = rows });
}).RequireRateLimiting("recommendation-admin");

app.MapGet("/api/admin/submissions/{id}/midi", async (string id, HttpRequest request, CancellationToken ct) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    if (!IsSafeId(id)) return Results.NotFound();
    var item = (await ReadPending(ct)).FirstOrDefault(x => x.Id == id);
    if (item is null) return Results.NotFound();
    var filePath = Path.Combine(submissionMidiDir, item.FileName);
    if (!File.Exists(filePath)) return Results.NotFound();
    return Results.File(filePath, "audio/midi", fileDownloadName: $"{SafeName(item.Title)}.mid", enableRangeProcessing: true);
}).RequireRateLimiting("recommendation-admin");

app.MapPost("/api/admin/submissions/{id}/approve", async (string id, HttpRequest request, CancellationToken ct) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    if (!IsSafeId(id)) return Results.NotFound();
    await gate.WaitAsync(ct);
    try
    {
        var pending = await ReadPendingUnlocked(ct);
        var submission = pending.FirstOrDefault(x => x.Id == id);
        if (submission is null) return Results.NotFound();
        var catalog = await ReadCatalogUnlocked(ct);
        if (catalog.Any(x => x.Id == id || x.Sha256 == submission.Sha256)) return Results.Conflict(new { error = "This submission is already published or an identical MIDI is published." });
        var publishedName = $"{id}.{submission.Sha256}.mid";
        var sourcePath = Path.Combine(submissionMidiDir, submission.FileName);
        var publishedPath = Path.Combine(midiDir, publishedName);
        if (!File.Exists(sourcePath)) return Results.Problem("Submission MIDI is missing.", statusCode: 500);
        File.Copy(sourcePath, publishedPath, true);
        var item = new Recommendation(id, submission.Title, "", submission.UploaderName, submission.Instrument, submission.DifficultyLevel, submission.DifficultyLabel, submission.Sha256, submission.SizeBytes, DateTimeOffset.UtcNow, publishedName);
        var updatedCatalog = catalog.Append(item).OrderBy(x => x.DifficultyLevel).ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ToList();
        try
        {
            await WriteCatalog(updatedCatalog, ct);
            await WritePending(pending.Where(x => x.Id != id).ToList(), ct);
        }
        catch
        {
            await WriteCatalog(catalog, ct);
            if (File.Exists(publishedPath)) File.Delete(publishedPath);
            throw;
        }
        if (File.Exists(sourcePath)) File.Delete(sourcePath);
        return Results.Ok(new { item = ToPublic(item) });
    }
    catch (IOException) { return Results.Problem("Unable to approve the submission.", statusCode: 500); }
    finally { gate.Release(); }
}).RequireRateLimiting("recommendation-admin");

app.MapDelete("/api/admin/submissions/{id}", async (string id, HttpRequest request, CancellationToken ct) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    if (!IsSafeId(id)) return Results.NotFound();
    await gate.WaitAsync(ct);
    try
    {
        var pending = await ReadPendingUnlocked(ct);
        var found = pending.FirstOrDefault(x => x.Id == id);
        if (found is null) return Results.NotFound();
        await WritePending(pending.Where(x => x.Id != id).ToList(), ct);
        var path = Path.Combine(submissionMidiDir, found.FileName);
        if (File.Exists(path)) File.Delete(path);
        return Results.NoContent();
    }
    catch (IOException) { return Results.Problem("Unable to reject the submission.", statusCode: 500); }
    finally { gate.Release(); }
}).RequireRateLimiting("recommendation-admin");

app.MapPost("/api/admin/recommendations", async (HttpRequest request, CancellationToken ct) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    if (!request.HasFormContentType) return Results.BadRequest(new { error = "multipart/form-data required." });
    var form = await request.ReadFormAsync(ct);
    var id = form["id"].ToString().Trim();
    var title = form["title"].ToString().Trim();
    var artist = form["artist"].ToString().Trim();
    var instrument = form["instrument"].ToString().Trim().ToLowerInvariant();
    var label = form["difficultyLabel"].ToString().Trim();
    if (id.Length == 0) id = Guid.NewGuid().ToString("N");
    if (!IsSafeId(id)) return Results.BadRequest(new { error = "id may contain only letters, digits, hyphen, and underscore (1-80 chars)." });
    if (title.Length is < 1 or > 160 || artist.Length > 160) return Results.BadRequest(new { error = "title is required (max 160); artist max 160." });
    if (instrument is not ("keyboard" or "guitar" or "piano")) return Results.BadRequest(new { error = "instrument must be keyboard, piano, or guitar." });
    if (!int.TryParse(form["difficultyLevel"], out var difficulty) || difficulty is < 1 or > 16) return Results.BadRequest(new { error = "difficultyLevel must be between 1 and 16." });
    if (label.Length > 40) return Results.BadRequest(new { error = "difficultyLabel max 40 characters." });
    var file = form.Files.GetFile("midi");
    if (file is null || file.Length < 1 || file.Length > MaxMidiBytes) return Results.BadRequest(new { error = "MIDI file is required and must be at most 20 MiB." });
    var extension = Path.GetExtension(file.FileName);
    if (!string.Equals(extension, ".mid", StringComparison.OrdinalIgnoreCase) && !string.Equals(extension, ".midi", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new { error = "Upload a .mid or .midi file." });
    await gate.WaitAsync(ct);
    try
    {
        var old = await ReadCatalogUnlocked(ct);
        var stagingPath = Path.Combine(midiDir, id + ".upload.tmp");
        await using (var output = new FileStream(stagingPath, FileMode.Create, FileAccess.Write, FileShare.None)) await file.CopyToAsync(output, ct);
        var bytes = await File.ReadAllBytesAsync(stagingPath, ct);
        if (bytes.Length < 4 || bytes[0] != (byte)'M' || bytes[1] != (byte)'T' || bytes[2] != (byte)'h' || bytes[3] != (byte)'d')
        {
            File.Delete(stagingPath);
            return Results.BadRequest(new { error = "The uploaded file does not have a valid MIDI header." });
        }
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var storageName = $"{id}.{hash}.mid";
        var destination = Path.Combine(midiDir, storageName);
        File.Move(stagingPath, destination, true);
        var now = DateTimeOffset.UtcNow;
        var updated = new Recommendation(id, title, artist, "", instrument, difficulty, label, hash, bytes.LongLength, now, storageName);
        var records = old.Where(x => x.Id != id).Append(updated).OrderBy(x => x.DifficultyLevel).ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ToList();
        try
        {
            await WriteCatalog(records, ct);
        }
        catch
        {
            if (!old.Any(x => x.Id == id && x.FileName == storageName) && File.Exists(destination)) File.Delete(destination);
            throw;
        }
        var prior = old.FirstOrDefault(x => x.Id == id);
        if (prior is not null && prior.FileName != storageName)
        {
            var priorPath = Path.Combine(midiDir, prior.FileName);
            if (File.Exists(priorPath))
            {
                try { File.Delete(priorPath); }
                catch (IOException) { /* Orphaned prior version is harmless; catalog already points at the committed version. */ }
            }
        }
        return Results.Ok(new { item = ToPublic(updated) });
    }
    catch (IOException) { return Results.Problem("Unable to store the MIDI file.", statusCode: 500); }
    finally { gate.Release(); }
}).RequireRateLimiting("recommendation-admin")
  .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxMidiBytes + 1024 * 1024));

app.MapDelete("/api/admin/recommendations/{id}", async (string id, HttpRequest request, CancellationToken ct) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    if (!IsSafeId(id)) return Results.NotFound();
    await gate.WaitAsync(ct);
    try
    {
        var all = await ReadCatalogUnlocked(ct);
        var found = all.FirstOrDefault(x => x.Id == id);
        if (found is null) return Results.NotFound();
        await WriteCatalog(all.Where(x => x.Id != id).ToList(), ct);
        var path = Path.Combine(midiDir, found.FileName);
        if (File.Exists(path)) File.Delete(path);
        return Results.NoContent();
    }
    catch (IOException) { return Results.Problem("Unable to delete the recommendation.", statusCode: 500); }
    finally { gate.Release(); }
}).RequireRateLimiting("recommendation-admin");

app.Run();

async Task<List<Recommendation>> ReadCatalog(CancellationToken ct)
{
    await gate.WaitAsync(ct);
    try { return await ReadCatalogUnlocked(ct); }
    finally { gate.Release(); }
}
async Task<List<PendingSubmission>> ReadPending(CancellationToken ct)
{
    await gate.WaitAsync(ct);
    try { return await ReadPendingUnlocked(ct); }
    finally { gate.Release(); }
}
async Task<List<PendingSubmission>> ReadPendingUnlocked(CancellationToken ct)
{
    if (!File.Exists(pendingPath)) return [];
    await using var stream = File.OpenRead(pendingPath);
    return await JsonSerializer.DeserializeAsync<List<PendingSubmission>>(stream, jsonOptions, ct) ?? [];
}
async Task WritePending(List<PendingSubmission> rows, CancellationToken ct)
{
    var temp = pendingPath + ".tmp";
    await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) await JsonSerializer.SerializeAsync(stream, rows, jsonOptions, ct);
    File.Move(temp, pendingPath, true);
}
async Task<List<Recommendation>> ReadCatalogUnlocked(CancellationToken ct)
{
    if (!File.Exists(catalogPath)) return [];
    try
    {
        await using var stream = File.OpenRead(catalogPath);
        var rows = await JsonSerializer.DeserializeAsync<List<Recommendation>>(stream, jsonOptions, ct) ?? [];
        return rows.Select(x => x with { UploaderName = x.UploaderName ?? "" }).ToList();
    }
    catch (JsonException) { throw; }
}
async Task WriteCatalog(List<Recommendation> rows, CancellationToken ct)
{
    var temp = catalogPath + ".tmp";
    await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) await JsonSerializer.SerializeAsync(stream, rows, jsonOptions, ct);
    File.Move(temp, catalogPath, true);
}
bool Authorized(HttpRequest request)
{
    var expected = Environment.GetEnvironmentVariable("NTE_GUITAR_ADMIN_TOKEN");
    var supplied = request.Headers["X-Admin-Token"].ToString();
    return !string.IsNullOrWhiteSpace(expected) && supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(supplied), System.Text.Encoding.UTF8.GetBytes(expected));
}
static bool IsSafeId(string id) => id.Length is > 0 and <= 80 && Regex.IsMatch(id, "^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);
static string SafeName(string value) => Regex.Replace(value, "[^\\p{L}\\p{N} _-]", "_").Trim() is { Length: > 0 } name ? name : "recommendation";
static object ToPublic(Recommendation x) => new { x.Id, x.Title, x.Artist, x.UploaderName, x.Instrument, x.DifficultyLevel, x.DifficultyLabel, x.Sha256, x.SizeBytes, x.UpdatedAt, midiUrl = $"/api/recommendations/{x.Id}/midi" };
static void AddLimit(RateLimiterOptions options, string name, int permits) => options.AddPolicy(name, context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));

public sealed record CurrentAnnouncementResponse(Announcement? Announcement);
public sealed record Recommendation(string Id, string Title, string Artist, string UploaderName, string Instrument, int DifficultyLevel, string DifficultyLabel, string Sha256, long SizeBytes, DateTimeOffset UpdatedAt, string FileName);
public sealed record PendingSubmission(string Id, string Title, string UploaderName, string Instrument, int DifficultyLevel, string DifficultyLabel, string Sha256, long SizeBytes, DateTimeOffset SubmittedAt, string FileName);
public sealed record Announcement
{
    public bool Enabled { get; init; }
    public string Id { get; init; } = "";
    public string Version { get; init; } = "";
    public string Title { get; init; } = "";
    public string Content { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public DateTimeOffset? PublishedAt { get; init; }
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 100) return "id is required and must be at most 100 characters.";
        if (string.IsNullOrWhiteSpace(Version) || Version.Length > 50) return "version is required and must be at most 50 characters.";
        if (string.IsNullOrWhiteSpace(Title) || Title.Length > 160) return "title is required and must be at most 160 characters.";
        if (string.IsNullOrWhiteSpace(Content) || Content.Length > 12000) return "content is required and must be at most 12000 characters.";
        if (DownloadUrl.Length > 2048 || (!string.IsNullOrWhiteSpace(DownloadUrl) && (!Uri.TryCreate(DownloadUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")))) return "downloadUrl must be an absolute HTTP or HTTPS URL.";
        return null;
    }
}






