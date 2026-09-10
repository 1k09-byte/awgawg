using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using stellarisKIT.Models;
using stellarisKIT.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace stellarisKIT.ViewModels;

public sealed partial class ThreadTunerViewModel : ObservableObject
{
    private readonly ProcessTuningService _tuning;
    private readonly CpuSetService _cpuSets;
    private readonly ThreadTuningService _threads;
    private readonly ProfileWatcherService _profiles;
    private readonly DispatcherQueue _dispatcher = null!;
    private readonly DispatcherQueueTimer _timer = null!;
    private bool _refreshing;

    public ObservableCollection<TunerProcessRow> Processes { get; } = new();
    public ObservableCollection<TunerProfile> Profiles { get; } = new();
    public ObservableCollection<TunerProfile> DisplayedProfiles { get; } = new();

    [ObservableProperty]
    private TunerProcessRow? _selectedProcess;

    [ObservableProperty]
    private TunerProfile? _selectedProfile;

    [ObservableProperty]
    private string _rulesFilter = string.Empty;

    public ThreadTunerViewModel()
    {
        // Safe defaults for designer / DI
        _tuning = App.Current.ProcessTuning;
        _cpuSets = App.Current.CpuSets;
        _threads = App.Current.ThreadTuning;
        _profiles = App.Current.ProfileWatcher;

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        if (_dispatcher != null)
        {
            _timer = _dispatcher.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(2);
            _timer.Tick += async (s, e) =>
            {
                // Never let a slow tick overlap the next one: two refreshes at
                // once double the UI churn and make the list feel frozen.
                if (_refreshing)
                {
                    return;
                }

                _refreshing = true;
                try
                {
                    await LoadProcessesAsync();
                    await RefreshCpuAsync();
                }
                finally
                {
                    _refreshing = false;
                }
            };
            _timer.Start();
            
            _ = LoadProcessesAsync();
        }

        // Setup base profiles binding
        foreach (var p in _profiles.ActiveProfiles)
        {
            Profiles.Add(p);
        }
        RefreshDisplayedProfiles();

        _profiles.RulesChanged += (_, _) => SyncProfiles();
    }

    /// <summary>
    /// Reconciles the bound collections with the service store. Always runs
    /// on the UI thread (the service raises RulesChanged there).
    /// </summary>
    public void SyncProfiles()
    {
        var selected = SelectedProfile;
        Profiles.Clear();
        foreach (var p in _profiles.ActiveProfiles)
        {
            Profiles.Add(p);
        }
        if (selected != null && Profiles.Contains(selected))
        {
            SelectedProfile = selected;
        }
        else
        {
            SelectedProfile = null;
        }
        RefreshDisplayedProfiles();
    }

    partial void OnRulesFilterChanged(string value) => RefreshDisplayedProfiles();

    public void RefreshDisplayedProfiles()
    {
        string filter = (RulesFilter ?? string.Empty).Trim();
        DisplayedProfiles.Clear();
        foreach (var p in Profiles)
        {
            if (filter.Length == 0
                || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || p.Pattern.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                DisplayedProfiles.Add(p);
            }
        }
        if (SelectedProfile != null && !DisplayedProfiles.Contains(SelectedProfile))
        {
            SelectedProfile = null;
        }
    }

    [RelayCommand]
    private async Task LoadProcessesAsync()
    {
        var active = await _tuning.ListProcessesAsync();
        
        _dispatcher.TryEnqueue(() => 
        {
            var livePids = new HashSet<int>(active.Select(p => p.Pid));

            foreach (var p in active)
            {
                var existing = Processes.FirstOrDefault(x => x.Pid == p.Pid);
                if (existing is null)
                {
                    // New process: take the row as enumerated.
                    Processes.Add(p);
                    if (!string.IsNullOrEmpty(p.Path))
                    {
                        _ = ExtractIconAsync(p);
                    }
                }
                else
                {
                    // Settings (priority, efficiency, affinity, state) can change
                    // at runtime — through this app or externally — so re-read the
                    // static fields on every poll. Otherwise the row keeps showing
                    // the value from when the process first appeared, making a
                    // successful change look like nothing happened. Each write is
                    // guarded so an unchanged value doesn't fire PropertyChanged
                    // and force a row re-render on every tick.
                    if (existing.PriorityText != p.PriorityText) existing.PriorityText = p.PriorityText;
                    if (existing.PriorityValue != p.PriorityValue) existing.PriorityValue = p.PriorityValue;
                    if (existing.AffinitySummary != p.AffinitySummary) existing.AffinitySummary = p.AffinitySummary;
                    if (existing.AffinityMask != p.AffinityMask) existing.AffinityMask = p.AffinityMask;
                    if (existing.EfficiencyMode != p.EfficiencyMode) existing.EfficiencyMode = p.EfficiencyMode;
                    if (existing.State != p.State) existing.State = p.State;
                    if (existing.Error != p.Error) existing.Error = p.Error;
                }
            }

            // Remove dead
            for (int i = Processes.Count - 1; i >= 0; i--)
            {
                if (!livePids.Contains(Processes[i].Pid))
                {
                    Processes.RemoveAt(i);
                }
            }
        });
    }

    private async Task ExtractIconAsync(TunerProcessRow row)
    {
        try
        {
            var sf = await Windows.Storage.StorageFile.GetFileFromPathAsync(row.Path);
            var tb = await sf.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem, 32);
            if (tb != null && tb.Size > 0)
            {
                _dispatcher.TryEnqueue(async () =>
                {
                    try 
                    {
                        var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                        await bmp.SetSourceAsync(tb);
                        row.AppIcon = bmp;
                    } catch { }
                });
            }
        }
        catch { }
    }

    private async Task RefreshCpuAsync()
    {
        if (Processes.Count == 0) return;
        
        // Pass the read-only projection directly to the tuning service
        await _tuning.SampleCpuAsync(Processes.ToList());
        _tuning.PruneCache(Processes.Select(p => p.Pid));
    }
}
