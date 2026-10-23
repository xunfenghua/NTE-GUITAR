using System;
using System.IO;
using System.Reflection;

namespace MidiAutoPlayer.Utils;

/// <summary>
/// 内置资源自解压与单文件运行保障引擎
/// 确保当用户脱离解压目录单独运行 NTE-GUITAR.exe 时，
/// 程序能够自动从自身嵌入式程序集资源中解压并加载网页 (HTML/CSS/JS) 及内置谱库。
/// </summary>
public static class EmbeddedAssetExtractor
{
    private static readonly object _lock = new();

    public static string EmbeddedAssetsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NTE-GUITAR", "embedded_assets");

    // Give each compiled EXE its own extracted web root. This avoids reusing an old
    // page bundle when users replace only the single-file executable in a portable folder.
    public static string EmbeddedWwwRoot => Path.Combine(
        EmbeddedAssetsRoot, "wwwroot", typeof(EmbeddedAssetExtractor).Assembly.ManifestModule.ModuleVersionId.ToString("N"));
    public static string EmbeddedScoresRoot => Path.Combine(EmbeddedAssetsRoot, "谱子");
    public static string EmbeddedSemiAutoOverlayRoot => Path.Combine(EmbeddedAssetsRoot, "Web", "SemiAutoOverlay");

    /// <summary>
    /// 确保内置的 wwwroot 网页、脚本及样式在单 exe 独立运行模式下已释放至持久缓存目录
    /// </summary>
    public static string EnsureWwwRootExtracted()
    {
        lock (_lock)
        {
            Directory.CreateDirectory(EmbeddedWwwRoot);
            ExtractPrefix("wwwroot", EmbeddedWwwRoot);
            return EmbeddedWwwRoot;
        }
    }

    public static string EnsureSemiAutoOverlayExtracted()
    {
        lock (_lock)
        {
            Directory.CreateDirectory(EmbeddedSemiAutoOverlayRoot);
            ExtractPrefix("Web\\SemiAutoOverlay", EmbeddedSemiAutoOverlayRoot);
            return EmbeddedSemiAutoOverlayRoot;
        }
    }

    /// <summary>
    /// 确保内置的谱子 (吉他 / 钢琴分类) 在单 exe 独立运行模式下已释放至持久缓存目录
    /// </summary>
    public static string EnsureScoresExtracted()
    {
        lock (_lock)
        {
            Directory.CreateDirectory(EmbeddedScoresRoot);
            ExtractPrefix("scores", EmbeddedScoresRoot);
            return EmbeddedScoresRoot;
        }
    }

    /// <summary>
    /// 从嵌入式资源流中直接读取指定资源数据 (例如视频或特定乐谱)
    /// </summary>
    public static byte[]? GetEmbeddedBytes(string resourceRelativePath)
    {
        var asm = typeof(EmbeddedAssetExtractor).Assembly;
        string normalized = resourceRelativePath.Replace('/', '\\');

        foreach (var resName in asm.GetManifestResourceNames())
        {
            string clean = resName.Replace('/', '\\');
            if (clean.EndsWith(normalized, StringComparison.OrdinalIgnoreCase))
            {
                using var stream = asm.GetManifestResourceStream(resName);
                if (stream != null)
                {
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }
        return null;
    }

    private static void ExtractPrefix(string prefix, string targetBaseDir)
    {
        var asm = typeof(EmbeddedAssetExtractor).Assembly;
        var allNames = asm.GetManifestResourceNames();

        foreach (var rawName in allNames)
        {
            string normalized = rawName.Replace('/', '\\');
            string prefixWithSlash = prefix + "\\";

            if (!normalized.StartsWith(prefixWithSlash, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string relativePath = normalized.Substring(prefixWithSlash.Length);
            string targetPath = Path.Combine(targetBaseDir, relativePath);

            using var stream = asm.GetManifestResourceStream(rawName);
            if (stream == null) continue;

            // 如果文件已存在且大小匹配，跳过重复写入以保证每次启动毫秒级瞬启
            if (File.Exists(targetPath))
            {
                try
                {
                    var fi = new FileInfo(targetPath);
                    if (fi.Length == stream.Length)
                    {
                        continue;
                    }
                }
                catch { }
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
                stream.CopyTo(fs);
            }
            catch { }
        }
    }
}
