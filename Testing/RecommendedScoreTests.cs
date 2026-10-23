using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MidiAutoPlayer.Models;
using MidiAutoPlayer.Services;

namespace MidiAutoPlayer.Testing;

public static class RecommendedScoreTests
{
    public static void RunAll(Action<bool, string, string> assert)
    {
        assert(MidiAutoPlayer.Models.RecommendedScore.NormalizeDifficultyLabel(7, null) == "7",
            "推荐谱难度标签为空时显示等级数字", "空标签应回退到等级文本");
        assert(MidiAutoPlayer.Models.RecommendedScore.NormalizeDifficultyLabel(7, "   ") == "7",
            "推荐谱空白难度标签回退到等级数字", "空白标签应回退到等级文本");
        assert(MidiAutoPlayer.Models.RecommendedScore.NormalizeDifficultyLabel(7, "困难") == "困难",
            "推荐谱保留中文难度标签", "非空标签不应被等级覆盖");
        assert(!MidiAutoPlayer.Models.RecommendedScore.IsValidDifficultyLevel(0) &&
               !MidiAutoPlayer.Models.RecommendedScore.IsValidDifficultyLevel(17) &&
               MidiAutoPlayer.Models.RecommendedScore.IsValidDifficultyLevel(16),
            "推荐谱等级限制为1至16", "边界验证失败");
        RunVerifiedDownloadAndCacheTest(assert).GetAwaiter().GetResult();
        RunSubmissionRequestTest(assert).GetAwaiter().GetResult();
    }

    private static async Task RunSubmissionRequestTest(Action<bool, string, string> assert)
    {
        string temp = Path.Combine(Path.GetTempPath(), "nte-recommended-submit-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string midiPath = Path.Combine(temp, "submission.mid");
        await File.WriteAllBytesAsync(midiPath, Encoding.ASCII.GetBytes("MThd\0\0\0\x06\0\x01\0\x01\x01\xe0"));
        bool sent = false;
        var handler = new DelegateHandler(async (request, _) =>
        {
            sent = request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/api/recommendations/submissions";
            var fields = await request.Content!.ReadAsStringAsync();
            sent &= fields.Contains("曲名", StringComparison.Ordinal) && fields.Contains("上传者", StringComparison.Ordinal) &&
                    fields.Contains("difficultyLevel", StringComparison.Ordinal) && fields.Contains("submission.mid", StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("{\"id\":\"submission_1\",\"status\":\"pending\"}", Encoding.UTF8, "application/json")
            };
        });
        try
        {
            using var httpClient = new HttpClient(handler);
            using var service = new RecommendedScoreService(temp, httpClient, "http://localhost:2888");
            var submission = await service.SubmitAsync("曲名", "上传者", "guitar", 7, "困难", midiPath);
            assert(sent && submission.Id == "submission_1" && submission.Status == "pending",
                "推荐谱投稿提交曲名、上传者、等级和 MIDI", "请求字段、投稿端点或审核状态不匹配服务端约定");
        }
        finally { try { Directory.Delete(temp, recursive: true); } catch { } }
    }

    private static async Task RunVerifiedDownloadAndCacheTest(Action<bool, string, string> assert)
    {
        byte[] midi = Encoding.ASCII.GetBytes("MThd\0\0\0\x06\0\x01\0\x01\x01\xe0MTrk\0\0\0\x04\0\xff\x2f\0");
        string hash = Convert.ToHexString(SHA256.HashData(midi)).ToLowerInvariant();
        int downloadCalls = 0;
        var handler = new DelegateHandler((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/midi", StringComparison.Ordinal) == true)
            {
                downloadCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(midi)
                });
            }

            string json = $$"""{"items":[{"id":"smoke_1","title":"Smoke","artist":"Local","instrument":"guitar","difficultyLevel":2,"difficultyLabel":"简单","sha256":"{{hash}}","sizeBytes":{{midi.Length}},"updatedAt":"2026-10-19T00:00:00Z","midiUrl":"/api/recommendations/smoke_1/midi"}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        });

        string tempDirectory = Path.Combine(Path.GetTempPath(), "nte-recommended-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var httpClient = new HttpClient(handler);
            using var service = new RecommendedScoreService(tempDirectory, httpClient, "http://localhost:2888");
            var listing = await service.GetCatalogAsync("Smoke", 2);
            var first = await service.DownloadAsync("smoke_1");
            var second = await service.DownloadAsync("smoke_1");
            assert(File.Exists(first.FilePath) && File.ReadAllBytes(first.FilePath).SequenceEqual(midi),
                "推荐谱 MIDI 下载后按哈希缓存", "缓存文件不存在或内容不一致");
            assert(!first.WasCached && second.WasCached && downloadCalls == 1,
                "推荐谱重复使用本地缓存", $"首次缓存={first.WasCached}，二次缓存={second.WasCached}，下载请求={downloadCalls}");
            assert(JsonSerializer.Serialize(listing).Contains("\"status\":\"ok\"", StringComparison.Ordinal),
                "推荐谱目录服务可返回结果", "目录返回为空");
        }
        finally
        {
            try { if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, recursive: true); } catch { }
        }
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
