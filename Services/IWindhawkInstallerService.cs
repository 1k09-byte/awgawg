using System;
using System.Threading;
using System.Threading.Tasks;
using stellarisKIT.Services;

namespace stellarisKIT.Services;

public interface IWindhawkInstallerService
{
    Task<(string Version, string DownloadUrl)> ResolveLatestOfflineInstallerAsync(CancellationToken ct = default);
    Task<string> DownloadInstallerAsync(string downloadUrl, IProgress<string>? status = null, CancellationToken ct = default);
    Task InstallSilentlyAsync(string installerPath, IProgress<string>? status = null, CancellationToken ct = default);
    Task<WindhawkInstallerService.WindhawkInstallerResult> ImportSettingsAsync(
        string jsonPath, IProgress<string>? status = null, CancellationToken ct = default);
    Task<WindhawkInstallerService.ModUpdateResult> UpdateModsIfAvailableAsync(
        string cliPath, string workingDirectory, IProgress<string>? status = null, CancellationToken ct = default);
    void CleanupTempFiles();
    static void CleanupFile(string path) => WindhawkInstallerService.CleanupFile(path);
}
