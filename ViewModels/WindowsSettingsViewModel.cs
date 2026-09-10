using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using stellarisKIT.Models;
using stellarisKIT.Services;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace stellarisKIT.ViewModels;

public partial class WindowsSettingsViewModel : ObservableObject
{
    private readonly MmcssRegistryService _mmcss = new();
    private MmcssCategorySettings? _saved;
    private bool _loading;

    /// <summary>Windhawk install + settings-import flow (see WindhawkProvisioningViewModel).</summary>
    public WindhawkProvisioningViewModel Windhawk { get; } = new();

    public ObservableCollection<MmcssCategoryItem> Categories { get; } = new();
    public string[] SchedulingCategories => MmcssValues.SchedulingCategories.ToArray();
    public string[] SfioPriorities => MmcssValues.SfioPriorities.ToArray();

    private MmcssCategoryItem? _selectedCategory;
    public MmcssCategoryItem? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value) && value != null)
                _ = LoadSelectedAsync(value.Name);
        }
    }
    // NumberBox.Value is a double in WinUI; keep the editor value as double
    // and validate/cast only when writing the DWORD registry value.
    [ObservableProperty] private double _priority;
    [ObservableProperty] private string _schedulingCategory = "Medium";
    [ObservableProperty] private string _sfioPriority = "Normal";
    [ObservableProperty] private bool _isConfigured;
    [ObservableProperty] private bool _mmcssServiceEnabled;
    [ObservableProperty] private bool _canEdit;
    [ObservableProperty] private bool _serializeTimerExpiration;
    [ObservableProperty] private bool _disableInterruptRouting;
    // Detection for the toggles this app writes: true when the registry value
    // exists (was applied by the app or manually), false when Windows defaults
    // are in effect. Surfaced in the card descriptions so the user can tell
    // "off" from "never touched".
    [ObservableProperty] private bool _serializeTimerExpirationConfigured;
    [ObservableProperty] private bool _disableInterruptRoutingConfigured;
    [ObservableProperty] private bool _mmcssServiceConfigured;

    public string SerializeTimerStatusText => SerializeTimerExpiration
        ? "On"
        : SerializeTimerExpirationConfigured ? "Off (explicitly set)" : "Off (Windows default)";

    public string DisableInterruptRoutingStatusText => DisableInterruptRouting
        ? "On"
        : DisableInterruptRoutingConfigured ? "Off (explicitly set)" : "Off (Windows default)";

    public string MmcssServiceStatusText => MmcssServiceEnabled
        ? "Enabled"
        : MmcssServiceConfigured ? "Disabled (explicit Start value)" : "Enabled (Windows default)";
    [ObservableProperty] private bool _powerSettingsLoading;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private InfoBarSeverity _messageSeverity = InfoBarSeverity.Informational;

    public bool IsAdministrator => _mmcss.IsAdministrator();

    partial void OnSerializeTimerExpirationChanged(bool value) => OnPropertyChanged(nameof(SerializeTimerStatusText));
    partial void OnSerializeTimerExpirationConfiguredChanged(bool value) => OnPropertyChanged(nameof(SerializeTimerStatusText));
    partial void OnDisableInterruptRoutingChanged(bool value) => OnPropertyChanged(nameof(DisableInterruptRoutingStatusText));
    partial void OnDisableInterruptRoutingConfiguredChanged(bool value) => OnPropertyChanged(nameof(DisableInterruptRoutingStatusText));
    partial void OnMmcssServiceEnabledChanged(bool value) => OnPropertyChanged(nameof(MmcssServiceStatusText));
    partial void OnMmcssServiceConfiguredChanged(bool value) => OnPropertyChanged(nameof(MmcssServiceStatusText));

    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            int serviceStart = await Task.Run(() => _mmcss.GetServiceStartType());
            MmcssServiceEnabled = serviceStart != 4;
            MmcssServiceConfigured = true; // GetServiceStartType falls back to 2 when Start is absent; still a read of a live key
            var power = await Task.Run(() => _mmcss.GetPowerSettings());
            SerializeTimerExpiration = power.SerializeTimerExpiration;
            DisableInterruptRouting = power.DisableInterruptRouting;
            SerializeTimerExpirationConfigured = power.SerializeTimerExpirationConfigured;
            DisableInterruptRoutingConfigured = power.DisableInterruptRoutingConfigured;
            CanEdit = IsAdministrator;
            var values = await Task.Run(() => _mmcss.GetTaskCategories());
            Categories.Clear();
            foreach (var item in values) Categories.Add(new MmcssCategoryItem(item.Name, item.IsEnabled));
            if (Categories.Count > 0) SelectedCategory = Categories[0];
            if (!IsAdministrator)
            {
                Message = "Run stellarisKIT as administrator to edit Windows MMCSS settings.";
                MessageSeverity = InfoBarSeverity.Warning;
                HasError = true;
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task LoadSelectedAsync(string name)
    {
        try
        {
            var settings = await Task.Run(() => _mmcss.GetCategorySettings(name));
            _saved = settings.Clone();
            Priority = settings.Priority;
            SchedulingCategory = settings.SchedulingCategory;
            SfioPriority = settings.SfioPriority;
            IsConfigured = settings.IsConfigured;
            ClearMessage();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (SelectedCategory == null) return;
        IsBusy = true;
        try
        {
            var settings = new MmcssCategorySettings
            {
                Name = SelectedCategory.Name, Priority = checked((uint)Priority),
                SchedulingCategory = SchedulingCategory, SfioPriority = SfioPriority,
                IsConfigured = IsConfigured
            };
            await Task.Run(() =>
            {
                // Category presence is not an official enable/disable switch.
                // Only write the documented task values; the service toggle above
                // is the supported global MMCSS enable/disable control.
                _mmcss.SaveCategorySettings(settings.Name, settings);
            });
            _saved = settings.Clone();
            SelectedCategory.IsEnabled = true;
            IsConfigured = true;
            Message = "MMCSS settings saved.";
            MessageSeverity = InfoBarSeverity.Success;
            HasError = false;
        }
        catch (Exception ex) { ShowError(ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Revert()
    {
        if (_saved == null) return;
        Priority = _saved.Priority; SchedulingCategory = _saved.SchedulingCategory;
        SfioPriority = _saved.SfioPriority; IsConfigured = _saved.IsConfigured;
        ClearMessage();
    }

    [RelayCommand]
    private async Task RestoreDefaultsAsync()
    {
        if (SelectedCategory == null) return;
        Priority = 6; SchedulingCategory = "Medium"; SfioPriority = "Normal"; IsConfigured = true;
        await SaveAsync();
    }

    private void ShowError(Exception ex)
    {
        Message = ex is UnauthorizedAccessException or System.Security.SecurityException
            ? "Administrator permission is required to change MMCSS settings."
            : ex.Message;
        MessageSeverity = InfoBarSeverity.Warning; HasError = true;
    }

    [RelayCommand]
    private async Task SavePowerSettingsAsync()
    {
        try
        {
            await Task.Run(() => _mmcss.SavePowerSettings(new WindowsPowerSettings
            {
                SerializeTimerExpiration = SerializeTimerExpiration,
                DisableInterruptRouting = DisableInterruptRouting,
            }));
            // The values are now explicitly configured in the registry.
            SerializeTimerExpirationConfigured = true;
            DisableInterruptRoutingConfigured = true;
            Message = "Power settings saved.";
            MessageSeverity = InfoBarSeverity.Success; HasError = false;
        }
        catch (Exception ex) { ShowError(ex); }
    }

    [RelayCommand]
    private async Task ToggleServiceAsync()
    {
        if (_loading) return;
        try
        {
            await Task.Run(() => _mmcss.SetServiceEnabled(MmcssServiceEnabled));
            Message = MmcssServiceEnabled ? "MMCSS is enabled (restart may be required)." : "MMCSS is disabled in the registry (restart may be required).";
            MessageSeverity = InfoBarSeverity.Success; HasError = false;
        }
        catch (Exception ex)
        {
            MmcssServiceEnabled = !MmcssServiceEnabled;
            ShowError(ex);
        }
    }

    private void ClearMessage()
    {
        if (!IsAdministrator)
        {
            Message = "Run stellarisKIT as administrator to edit Windows MMCSS settings.";
            MessageSeverity = InfoBarSeverity.Warning;
            HasError = true;
            return;
        }
        Message = string.Empty;
        HasError = false;
    }
}

