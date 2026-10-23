using System.Windows.Forms;

namespace MidiAutoPlayer.Services;

public sealed record ImportedWallpaper(string FileName, string MediaType, string MediaUrl);

public sealed class WallpaperService
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".webm", ".mov", ".m4v"
    };

    public string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NTE-GUITAR");

    public string WallpaperDirectory => Path.Combine(DataDirectory, "Wallpapers");

    public async Task<ImportedWallpaper?> ImportAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择界面壁纸",
            Filter = "图片或视频|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.mp4;*.webm;*.mov;*.m4v|图片|*.jpg;*.jpeg;*.png;*.webp;*.bmp|视频|*.mp4;*.webm;*.mov;*.m4v",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != DialogResult.OK)
            return null;

        string sourcePath = dialog.FileName;
        string extension = Path.GetExtension(sourcePath);
        string mediaType = ImageExtensions.Contains(extension)
            ? "image"
            : VideoExtensions.Contains(extension) ? "video" : throw new InvalidOperationException("不支持此图片或视频格式。");

        Directory.CreateDirectory(WallpaperDirectory);
        string fileName = $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
        string destination = Path.Combine(WallpaperDirectory, fileName);
        try
        {
            await Task.Run(() => File.Copy(sourcePath, destination, overwrite: false));
        }
        catch
        {
            try { if (File.Exists(destination)) File.Delete(destination); } catch { }
            throw;
        }
        return new ImportedWallpaper(fileName, mediaType, GetMediaUrl(fileName));
    }

    public bool IsManagedAsset(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
            return false;

        string extension = Path.GetExtension(fileName);
        return (ImageExtensions.Contains(extension) || VideoExtensions.Contains(extension)) &&
            File.Exists(Path.Combine(WallpaperDirectory, fileName));
    }

    public string GetMediaType(string fileName)
    {
        string extension = Path.GetExtension(fileName);
        return ImageExtensions.Contains(extension) ? "image" : VideoExtensions.Contains(extension) ? "video" : "";
    }

    public string GetMediaUrl(string fileName) => $"https://nte-media.local/{Uri.EscapeDataString(fileName)}";
}
