using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;

namespace MidiAutoPlayer.Services;

public static class VirtualGamepadRepairService
{
    private const string InstallerName = "ViGEmBus_1.22.0_x64_x86_arm64.exe";
    private const string InstallerUrl = "https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/ViGEmBus_1.22.0_x64_x86_arm64.exe";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<object?> RepairAsync()
    {
        await Gate.WaitAsync();
        try
        {
            string packaged = Path.Combine(AppContext.BaseDirectory, "Dependencies", InstallerName);
            string installer = packaged;
            if (!File.Exists(installer))
            {
                string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NTE-GUITAR", "Dependencies");
                Directory.CreateDirectory(appData);
                installer = Path.Combine(appData, InstallerName);
                if (!File.Exists(installer)) await DownloadInstallerAsync(installer);
            }

            VerifySigner(installer);
            var start = new ProcessStartInfo(installer) { UseShellExecute = true, Verb = "runas" };
            Process? process;
            try { process = Process.Start(start); }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return new { started = false, canceled = true, message = "已取消管理员授权。" };
            }
            if (process is null) throw new InvalidOperationException("无法启动 ViGEmBus 安装程序。");
            process.Dispose();
            return new { started = true, canceled = false, message = "ViGEmBus 安装/修复程序已启动，请完成 Windows 安装向导。" };
        }
        finally { Gate.Release(); }
    }

    private static async Task DownloadInstallerAsync(string destination)
    {
        string partial = destination + ".download";
        try
        {
            using var response = await Http.GetAsync(InstallerUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
            await input.CopyToAsync(output);
            output.Close();
            VerifySigner(partial);
            File.Move(partial, destination, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
            throw;
        }
    }

    private static void VerifySigner(string path)
    {
        using X509Certificate2 certificate = new(X509Certificate.CreateFromSignedFile(path));
        string signer = certificate.GetNameInfo(X509NameType.SimpleName, false);
        if (!signer.Contains("Nefarius Software Solutions", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ViGEmBus 安装包的签名发布者校验失败。");
    }
}
