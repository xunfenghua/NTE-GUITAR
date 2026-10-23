using System.Net.Http.Json;
using System.Text.Json;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MidiAutoPlayer.Library;
using MidiAutoPlayer.Midi;
using MidiAutoPlayer.Models;
using MidiAutoPlayer.Services;

namespace MidiAutoPlayer.Testing;

public static class RecommendedScoreIntegrationSmoke
{
    public static async Task<int> RunAsync()
    {
        string baseUrl = Environment.GetEnvironmentVariable("NTE_GUITAR_RECOMMENDED_SERVER_URL") ?? "";
        string token = Environment.GetEnvironmentVariable("NTE_GUITAR_ADMIN_TOKEN") ?? "";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var serverUri) || string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine("Set NTE_GUITAR_RECOMMENDED_SERVER_URL and NTE_GUITAR_ADMIN_TOKEN for the local integration smoke.");
            return 2;
        }

        string tempRoot = Path.Combine(Path.GetTempPath(), "nte-recommended-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string midiPath = Path.Combine(tempRoot, "client-smoke.mid");
        string? submissionId = null;
        bool published = false;
        try
        {
            CreateMidi(midiPath);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            string cacheDirectory = Path.Combine(tempRoot, "recommended-cache");
            using var recommendationService = new RecommendedScoreService(cacheDirectory, http, baseUrl);
            var submission = await recommendationService.SubmitAsync("Client smoke", "Local uploader", "guitar", 2, "联通测试", midiPath);
            submissionId = submission.Id;
            if (submission.Status != "pending") throw new InvalidOperationException("Client submission did not enter the pending review state.");
            Console.WriteLine("[PASS] Client submitted a MIDI and received pending review status.");

            var beforeApproval = await recommendationService.GetCatalogAsync("Client smoke", 2);
            using (var hiddenJson = JsonDocument.Parse(JsonSerializer.Serialize(beforeApproval)))
            {
                if (hiddenJson.RootElement.GetProperty("items").GetArrayLength() != 0)
                    throw new InvalidOperationException("Unreviewed client submission appeared in the public catalog.");
            }
            Console.WriteLine("[PASS] Pending client submission remains hidden from the public recommendation list.");

            using (var pendingRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(serverUri, "api/admin/submissions")))
            {
                pendingRequest.Headers.Add("X-Admin-Token", token);
                using var pendingResponse = await http.SendAsync(pendingRequest);
                pendingResponse.EnsureSuccessStatusCode();
                using var pendingJson = JsonDocument.Parse(await pendingResponse.Content.ReadAsStringAsync());
                if (!pendingJson.RootElement.GetProperty("items").EnumerateArray().Any(item => item.GetProperty("id").GetString() == submissionId))
                    throw new InvalidOperationException("Submitted MIDI is missing from the authenticated review queue.");
            }
            using (var approve = new HttpRequestMessage(HttpMethod.Post, new Uri(serverUri, $"api/admin/submissions/{Uri.EscapeDataString(submissionId)}/approve")))
            {
                approve.Headers.Add("X-Admin-Token", token);
                using var approved = await http.SendAsync(approve);
                approved.EnsureSuccessStatusCode();
            }
            published = true;
            Console.WriteLine("[PASS] Admin approval published the submitted score.");

            var catalog = await recommendationService.GetCatalogAsync("Client smoke", 2);
            using var catalogJson = JsonDocument.Parse(JsonSerializer.Serialize(catalog));
            var listed = catalogJson.RootElement.GetProperty("items").EnumerateArray()
                .FirstOrDefault(item => item.GetProperty("id").GetString() == submissionId);
            if (listed.ValueKind == JsonValueKind.Undefined)
                throw new InvalidOperationException("Uploaded score did not appear in the filtered client catalog.");
            if (listed.GetProperty("uploaderName").GetString() != "Local uploader")
                throw new InvalidOperationException("Approved score did not preserve the uploader name in the public catalog.");
            Console.WriteLine("[PASS] Client service fetched and filtered the recommendation catalog.");

            var first = await recommendationService.DownloadAsync(submissionId!);
            var second = await recommendationService.DownloadAsync(submissionId!);
            if (first.WasCached || !second.WasCached)
                throw new InvalidOperationException("Client cache state did not change after the first verified download.");
            var (sha256, notes, _, _) = MidiLoader.Load(first.FilePath);
            if (notes.Count == 0 || !string.Equals(sha256, first.Score.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Downloaded client cache did not parse as the uploaded MIDI or its checksum differs.");
            Console.WriteLine("[PASS] Client service verified, parsed and reused the cached MIDI.");

            var library = new SongLibrary(Path.Combine(tempRoot, "library"));
            var libraryService = new LibraryService(library);
            object conversion = libraryService.ConvertAndSave(
                first.FilePath, first.Score.Title, targetInstrument: 1, transpose: 0,
                outOfRangeMode: "OctaveFold", filterDrumTrack: true,
                enableMelodyExtraction: false, melodyStrategy: "FullSong");
            using var convertedJson = JsonDocument.Parse(JsonSerializer.Serialize(conversion));
            string songId = convertedJson.RootElement.GetProperty("id").GetString() ?? "";
            var convertedSong = library.LoadSong(songId);
            if (convertedSong is null || convertedSong.NoteCount == 0)
                throw new InvalidOperationException("Recommended MIDI did not convert into a usable local library song.");
            Console.WriteLine("[PASS] Downloaded recommendation converted into a playable local library song.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[FAIL] Recommended score client/server integration: " + ex.Message);
            return 1;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(submissionId))
            {
                try
                {
                    string deletePath = published
                        ? "api/admin/recommendations/" + Uri.EscapeDataString(submissionId)
                        : "api/admin/submissions/" + Uri.EscapeDataString(submissionId);
                    using var delete = new HttpRequestMessage(HttpMethod.Delete, new Uri(serverUri, deletePath));
                    delete.Headers.Add("X-Admin-Token", token);
                    using var response = await new HttpClient().SendAsync(delete);
                    if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
                        Console.Error.WriteLine($"[WARN] Could not remove smoke entry (HTTP {(int)response.StatusCode}).");
                }
                catch (Exception ex) { Console.Error.WriteLine("[WARN] Could not clean up smoke entry: " + ex.Message); }
            }
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static void CreateMidi(string path)
    {
        var midi = new MidiFile { TimeDivision = new TicksPerQuarterNoteTimeDivision(480) };
        var track = new TrackChunk();
        track.Events.Add(new SetTempoEvent(500_000) { DeltaTime = 0 });
        foreach (byte note in new byte[] { 52, 55, 59, 64, 67, 64, 59, 55 })
        {
            track.Events.Add(new NoteOnEvent((SevenBitNumber)note, (SevenBitNumber)100) { DeltaTime = 0 });
            track.Events.Add(new NoteOffEvent((SevenBitNumber)note, (SevenBitNumber)0) { DeltaTime = 480 });
        }
        midi.Chunks.Add(track);
        midi.Write(path, true);
    }
}

