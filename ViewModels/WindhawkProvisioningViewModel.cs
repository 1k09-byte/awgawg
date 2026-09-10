using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using stellarisKIT.Models;
using stellarisKIT.Services;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.ObjectModel;

namespace stellarisKIT.ViewModels;

/// <summary>
/// Drives the "Install Windhawk &amp; import settings" flow on the WS page:
/// detection → user confirmation dialog → download/silent install (if needed)
/// → backup import (via windhawk-cli data import), then optional mod updates,
/// with progress reporting and a per-mod summary.
/// </summary>
public partial class WindhawkProvisioningViewModel : ObservableObject
{
    private readonly WindhawkDetectionService _detection = new();
    private readonly WindhawkProvisioningService _provisioning = new();
    private readonly WindhawkInstallerService _installer = new();
    private readonly WindhawkImportService _import = new();
    private CancellationTokenSource? _cts;

    [ObservableProperty] private WindhawkInstallationInfo _installation = WindhawkInstallationInfo.NotInstalled;
    [ObservableProperty] private string? _backupFilePath;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private double? _downloadPercent;
    [ObservableProperty] private bool _hasMessage;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private InfoBarSeverity _messageSeverity = InfoBarSeverity.Informational;

    /// <summary>
    /// True when the user has asked to use the bundled default mod list instead of
    /// a manually selected file. In that case BackupFilePath stays null and the
    /// import path uses the bundled asset copied to a writable location.
    /// </summary>
    [ObservableProperty] private bool _useBundledDefaults = false;

    /// <summary>
    /// The file that will actually be imported. When UseBundledDefaults is true,
    /// this is the writable copy of the bundled asset; otherwise it is
    /// BackupFilePath.
    /// </summary>
    public string? EffectiveBackupFilePath => UseBundledDefaults
        ? WindhawkModCatalog.EnsureBundledBackupOnDisk()
        : BackupFilePath;

    public bool IsInstalled => Installation.IsInstalled;
    public bool IsCliAvailable => Installation.CliPath is not null;
    public string InstallStatusText => Installation.IsInstalled
        ? $"Installed{(!string.IsNullOrEmpty(Installation.Version) ? " · v" + Installation.Version : "")}"
        : "Not installed";

    partial void OnInstallationChanged(WindhawkInstallationInfo value)
    {
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(IsCliAvailable));
        OnPropertyChanged(nameof(InstallStatusText));
    }

    partial void OnBackupFilePathChanged(string? value)
    {
        OnPropertyChanged(nameof(BackupFileName));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(EffectiveBackupFilePath));
    }

    partial void OnUseBundledDefaultsChanged(bool value)
    {
        OnPropertyChanged(nameof(BackupFileName));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(EffectiveBackupFilePath));
    }

    public string BackupFileName => UseBundledDefaults
        ? "Bundled default mod list"
        : (string.IsNullOrEmpty(BackupFilePath) ? "No file selected" : Path.GetFileName(BackupFilePath));

    public bool CanRun => !IsBusy && !string.IsNullOrEmpty(EffectiveBackupFilePath);

    public void RefreshDetection()
    {
        Installation = _detection.Detect();
        if (Installation.IsInstalled)
        {
            ShowMessage($"Windhawk {Installation.Version ?? ""} detected.".Trim() + (Installation.CliPath is not null
                ? " Settings import will use its built-in CLI."
                : " Settings import will use the legacy registry path (Windhawk 2.0's CLI is not present)."),
                InfoBarSeverity.Informational);
        }
        else
        {
            ShowMessage("Windhawk is not installed. Running the flow below installs the latest release silently, then imports the selected backup.",
                InfoBarSeverity.Informational);
        }
    }

    public void ShowMessage(string message, InfoBarSeverity severity)
    {
        Message = message;
        MessageSeverity = severity;
        HasMessage = true;
    }

    private void ClearMessage() => HasMessage = false;

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    /// <summary>
    /// The whole flow, awaited by the page after the confirmation ContentDialog
    /// resolves. Runs install (when missing) and import sequentially with
    /// progress surfaced through observable properties.
    /// </summary>
    public async Task RunProvisioningAsync()
    {
        var path = EffectiveBackupFilePath;
        if (string.IsNullOrEmpty(path)) return;

        IsBusy = true;
        ClearMessage();
        DownloadPercent = null;
        StatusText = "Starting...";
        _cts = new CancellationTokenSource();

        try
        {
            if (!Installation.IsInstalled)
            {
                if (!WindhawkDetectionService.IsRunningElevated())
                {
                    ShowMessage("Administrator rights are required to install Windhawk. Run stellarisKIT elevated and try again.",
                        InfoBarSeverity.Error);
                    return;
                }

                var installProgress = new Progress<WindhawkProvisioningService.ProvisionProgress>(p =>
                {
                    StatusText = p.StatusText;
                    DownloadPercent = p.DownloadPercent;
                });

                StatusText = "Preparing installation...";
                Installation = await _provisioning.EnsureInstalledAsync(installProgress, _cts.Token);
            }

            // Prefer the verified CLI import path (Windhawk 2.0+): download the
            // offline installer, install it silently, then import via windhawk-cli
            // data import, and apply any available mod updates.
            var cliProgress = new Progress<WindhawkProvisioningService.ProvisionProgress>(p =>
            {
                StatusText = p.StatusText;
                DownloadPercent = p.DownloadPercent;
            });

            StatusText = UseBundledDefaults
                ? "Importing bundled default mod list..."
                : "Importing settings...";
            DownloadPercent = null;

            var importStatus = new Progress<string>(s => StatusText = s);
            var importResult = await _import.ImportBackupAsync(path!, Installation, importStatus, _cts.Token);
            
            ShowMessage(importResult.SummaryText, importResult.Skipped > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            
            StatusText = "Done.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
            ShowMessage("Operation cancelled.", InfoBarSeverity.Warning);
        }
        catch (UnauthorizedAccessException ex)
        {
            StatusText = "Failed.";
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            StatusText = "Failed.";
            ShowMessage($"Windhawk provisioning failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
            DownloadPercent = null;
            _cts?.Dispose();
            _cts = null;
            // Re-detect: an install just happened (or state changed underneath us).
            Installation = _detection.Detect();

            // After a successful import of the bundled list, keep UseBundledDefaults
            // true so the next run is reproducible; the user can still switch to a
            // custom file if they want.
            if (string.IsNullOrEmpty(Message) || Message.Contains("Imported"))
            {
                // Nothing to do — leave UseBundledDefaults as the user set it.
            }
        }
    }
}
