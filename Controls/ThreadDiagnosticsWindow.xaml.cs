using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using stellarisKIT.Models;
using stellarisKIT.Native;
using stellarisKIT.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WinRT.Interop;

namespace stellarisKIT.Controls
{
    public sealed partial class ThreadDiagnosticsWindow : Window
    {
        private int _pid;
        private string _processName;
        private DispatcherTimer _timer;
        private readonly NativeSnapshotService _snapshot = new();
        private readonly ThreadSymbolResolver _symbols = new();
        // Shared app-wide instance: the main page and this window share one
        // restore map so neither can clobber the other's changes.
        // Shared app-wide instance: the main page and this window share one
        // restore map so neither UI can clobber the other's changes.
        private readonly Services.GamingModeService _gamingMode = App.Current.GamingMode;
        // Per-window detector: each Threads window samples its own target, so
        // detection state must not be shared app-wide.
        private readonly Services.CpuBoundDetector _cpuDetector = new();
        /// <summary>Last whole-process CPU% (of one core) fed into the detection state machine.</summary>
        private double _lastProcessCpu = -1;
        /// <summary>Guards against re-entrant auto-raise while a previous one is still running.</summary>
        private bool _autoRaiseInFlight;
        /// <summary>True once the current detection episode has auto-raised the target.</summary>
        private bool _autoRaiseApplied;
        /// <summary>Per-pid total kernel+user ticks baseline for whole-process CPU%.</summary>
        private readonly Dictionary<int, long> _lastProcessTotalTicks = new();
        /// <summary>Own timestamp for the process-CPU calc — UpdateUsageDeltas advances the shared one.</summary>
        private DateTime _lastProcessCpuSampleUtc = DateTime.UtcNow;
        private readonly Dictionary<int, long> _lastTicks = new();
        private readonly Dictionary<int, long> _lastSwitches = new();
        private readonly Dictionary<int, long> _lastCycles = new();
        private DateTime _lastSampleUtc = DateTime.UtcNow;

        public ObservableCollection<ThreadDiagnosticRow> Threads { get; } = new();

        public ThreadDiagnosticsWindow(int pid, string processName)
        {
            this.InitializeComponent();
            _pid = pid;
            _processName = processName;
            
            ProcessNameText.Text = _processName;            PidText.Text = $"PID {_pid}";
            bool isGame = App.Current.ProfileWatcher.FindMatches(_processName)
                .Any(profile => profile.GamingModeAuto);
            GamingModePanel.Visibility = isGame ? Visibility.Visible : Visibility.Collapsed;
            
            // OS specific native styling
            var hWnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            
            this.ExtendsContentIntoTitleBar = true;
            this.SetTitleBar(null);

            // Same Mica (BaseAlt) backdrop as the main window. The XAML root
            // is transparent so it shows through; inner layer-fill panels
            // keep their brushes for contrast.
            if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
            {
                this.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop
                {
                    Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt,
                };
            }
            else if (Application.Current.Resources.TryGetValue(
                "ApplicationPageBackgroundThemeBrush", out object? bg)
                && bg is Microsoft.UI.Xaml.Media.Brush brush)
            {
                RootGrid.Background = brush;
            }
            
            appWindow.Title = $"{_processName} ({_pid}) - Threads - stellarisKIT";
            appWindow.Resize(new Windows.Graphics.SizeInt32 { Width = 1320, Height = 700 });
            
            // Standard placement in center of screen
            var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
            if (displayArea != null)
            {
                var centeredPosition = new Windows.Graphics.PointInt32
                {
                    X = ((displayArea.WorkArea.Width - appWindow.Size.Width) / 2),
                    Y = ((displayArea.WorkArea.Height - appWindow.Size.Height) / 2)
                };
                appWindow.Move(centeredPosition);
            }

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += (s, e) => RefreshThreads();
            _timer.Start();

            // Immediately load data
            RefreshThreads();
        }

        private void Window_Closed(object sender, WindowEventArgs args)
        {
            _timer?.Stop();
            // Never leave the system boosted: undo gaming mode lowering and
            // any CPU-bound auto-raise when the window goes away.
            _gamingMode.Deactivate();
        }

        private void RefreshThreads()
        {
            try
            {
                using var proc = Process.GetProcessById(_pid);
                var currentTids = new System.Collections.Generic.HashSet<int>();

                // One native snapshot per tick for the delta columns. Start
                // addresses are queried per thread below using class 9; the
                // snapshot StartAddress is often only RtlUserThreadStart.
                var snapshot = _snapshot.TrySnapshot().FirstOrDefault(p => p.ProcessId == (uint)_pid);
                
                foreach (ProcessThread t in proc.Threads)
                {
                    currentTids.Add(t.Id);
                    var existing = Threads.FirstOrDefault(x => x.Tid == t.Id);
                    
                    if (existing != null)
                    {
                        try 
                        {
                            // Guarded writes keep the per-tick re-render churn
                            // down for the (common) case where nothing changed.
                            if (existing.Base != t.BasePriority) existing.Base = t.BasePriority;
                            if (existing.Dynamic != t.CurrentPriority) existing.Dynamic = t.CurrentPriority;
                            string state = GetThreadState(t);
                            if (existing.State != state) existing.State = state;
                            string relative = FormatRelative((int)t.PriorityLevel);
                            if (existing.Relative != relative) existing.Relative = relative;
                            RefreshThreadSettings(existing);

                            long realStart = NativeSnapshotService.TryGetWin32StartAddress((uint)t.Id)
                                ?? TryGetSnapshotStart(snapshot, t.Id)
                                ?? 0;

                            // A thread can gain a description after it starts
                            // (e.g. a named .NET worker thread), so refresh it.
                            string desc = ResolveThreadDescription(t.Id, proc, realStart);
                            if (existing.Description != desc) existing.Description = desc;
                            string start = GetStartAddress(proc, t, realStart);
                            if (existing.StartAddress != start) existing.StartAddress = start;
                            if (existing.StartAddressValue != realStart) existing.StartAddressValue = realStart;
                        } 
                        catch { }
                    }
                    else
                    {
                        var row = new ThreadDiagnosticRow
                        {
                            Tid = t.Id,
                            StartAddress = "-",
                            CpuText = "-",
                            SwitchesText = "-",
                            CyclesText = "-"
                        };

                        try
                        {
                            long realStart = NativeSnapshotService.TryGetWin32StartAddress((uint)t.Id)
                                ?? TryGetSnapshotStart(snapshot, t.Id)
                                ?? 0;
                            row.StartAddressValue = realStart;
                            row.Description = ResolveThreadDescription(t.Id, proc, realStart);

                            row.StartAddress = GetStartAddress(proc, t, realStart);
                            row.Base = t.BasePriority;
                            row.Dynamic = t.CurrentPriority;
                            row.State = GetThreadState(t);
                            row.Relative = FormatRelative((int)t.PriorityLevel);
                            RefreshThreadSettings(row);
                        } 
                        catch { }
                        
                        Threads.Add(row);
                    }
                }

                for (int i = Threads.Count - 1; i >= 0; i--)
                {
                    if (!currentTids.Contains(Threads[i].Tid))
                        Threads.RemoveAt(i);
                }

                // Keep the grid organized: named role threads are pinned to
                // the top (TID order within the group), unnamed threads
                // below (also by TID), instead of jumbling in
                // process-enumeration order. Moves preserve the current
                // selection and scroll position.
                var ordered = Threads.OrderBy(x => IsUnnamed(x.Description) ? 1 : 0).ThenBy(x => x.Tid).ToList();
                for (int i = 0; i < ordered.Count; i++)
                {
                    int current = Threads.IndexOf(ordered[i]);
                    if (current != i)
                    {
                        Threads.Move(current, i);
                    }
                }

                UpdateUsageDeltas(snapshot);
                ThreadCountText.Text = $"{Threads.Count} threads   |   2 s sample";

                // Whole-process CPU sample for CPU-bound detection. Percent of
                // one core: a single-threaded game pegging one core reads ~100
                // regardless of machine size, which is the regime where raising
                // priority actually changes scheduling.
                double processCpu = ComputeProcessCpuPercent(snapshot, proc);
                _lastProcessCpu = processCpu;
                UpdateGamingModeUi(processCpu);
            }
            catch
            {
                ThreadCountText.Text = "Process exited or access denied.";
            }
        }

        /// <summary>
        /// Fills the CPU / context switches / cycles delta columns from the native
        /// process snapshot (per-thread context switches + kernel/user ticks) plus
        /// QueryThreadCycleTime. The first sample only seeds baselines and leaves
        /// the "-" placeholders in place; real deltas appear from the second tick
        /// onwards, so the grid never flashes fake zeros while "detecting".
        /// Keeps "-" when the snapshot can't see the process (e.g. missing
        /// privileges) instead of lying with zeros.
        /// </summary>
        private void UpdateUsageDeltas(SnapshotProcess? snapshot)
        {
            try
            {
                if (snapshot is null || Threads.Count == 0)
                {
                    return;
                }

                var now = DateTime.UtcNow;
                double intervalMs = Math.Max(1, (now - _lastSampleUtc).TotalMilliseconds);
                _lastSampleUtc = now;

                // Last-wins map: the snapshot can carry stray duplicate entries,
                // and ToDictionary would throw and kill the whole update.
                var byTid = new Dictionary<int, SnapshotThread>();
                foreach (var t in snapshot.Threads)
                {
                    byTid[(int)t.ThreadId] = t;
                }

                foreach (var row in Threads)
                {
                    if (!byTid.TryGetValue(row.Tid, out var thread))
                    {
                        continue;
                    }

                    long totalTicks = thread.KernelTicks + thread.UserTicks;
                    long switches = thread.ContextSwitches;
                    long cycles = GetThreadCycleTime(row.Tid);

                    // Only publish values once a previous baseline exists.
                    if (_lastTicks.ContainsKey(row.Tid)
                        && _lastSwitches.ContainsKey(row.Tid)
                        && _lastCycles.ContainsKey(row.Tid))
                    {
                        double cpuPercent = Math.Clamp(((totalTicks - _lastTicks[row.Tid]) / 10000.0) / intervalMs * 100.0, 0, 100);
                        long switchDelta = Math.Max(0, switches - _lastSwitches[row.Tid]);
                        long cycleDelta = Math.Max(0, cycles - _lastCycles[row.Tid]);

                        // Guarded writes: same-value deltas shouldn't re-render rows.
                        string cpuText = cpuPercent.ToString("0.0");
                        if (row.CpuText != cpuText) row.CpuText = cpuText;

                        string switchesText = switchDelta.ToString("N0");
                        if (row.SwitchesText != switchesText) row.SwitchesText = switchesText;

                        string cyclesText = cycleDelta.ToString("N0");
                        if (row.CyclesText != cyclesText) row.CyclesText = cyclesText;
                    }

                    _lastTicks[row.Tid] = totalTicks;
                    _lastSwitches[row.Tid] = switches;
                    _lastCycles[row.Tid] = cycles;
                }
            }
            catch
            {
                // Snapshot unavailable: leave the "-" placeholders in place.
            }
        }

        /// <summary>
        /// Whole-process CPU usage as percent of ONE core, from the native
        /// snapshot's per-thread tick deltas (no extra P/Invoke). Returns -1
        /// when there is no baseline yet or the snapshot can't see the process.
        /// </summary>
        private double ComputeProcessCpuPercent(SnapshotProcess? snapshot, Process proc)
        {
            if (snapshot is null)
            {
                return -1;
            }

            long totalTicks = 0;
            foreach (var t in snapshot.Threads)
            {
                totalTicks += t.KernelTicks + t.UserTicks;
            }

            if (!_lastProcessTotalTicks.TryGetValue(_pid, out long prev))
            {
                _lastProcessTotalTicks[_pid] = totalTicks;
                return -1; // first sample only seeds the baseline
            }

            double intervalMs = Math.Max(1, (DateTime.UtcNow - _lastProcessCpuSampleUtc).TotalMilliseconds);
            _lastProcessCpuSampleUtc = DateTime.UtcNow;
            long delta = totalTicks - prev;
            _lastProcessTotalTicks[_pid] = totalTicks;

            // 100 ns ticks → ms; divide by interval → percent of one core.
            double percent = ((delta / 10000.0) / intervalMs) * 100.0;
            return Math.Clamp(percent, 0, 100 * Environment.ProcessorCount);
        }

        /// <summary>
        /// Drives the CPU-bound indicator, auto-raise and Gaming mode status
        /// line. Called once per refresh tick with the latest process CPU%.
        /// </summary>
        private async void UpdateGamingModeUi(double processCpu)
        {
            var state = _cpuDetector.Observe(processCpu);

            string indicator;
            switch (state)
            {
                case Services.CpuBoundState.Detected:
                    indicator = "CPU-bound detected";
                    break;
                case Services.CpuBoundState.Sampling:
                    indicator = processCpu >= 0 ? $"CPU {processCpu:0}% · detecting…" : "detecting…";
                    break;
                default:
                    indicator = processCpu >= 0 ? $"CPU {processCpu:0}% · not CPU-bound" : "";
                    break;
            }

            string text = indicator;
            if (_gamingMode.IsActive)
            {
                text += "   |   Gaming mode ON";
            }

            if (CpuBoundText.Text != text) CpuBoundText.Text = text;

            // Auto-raise fires exactly once per detection episode, never while
            // Gaming mode is already handling priorities.
            if (state == Services.CpuBoundState.Detected && !_gamingMode.IsActive && !_autoRaiseApplied && !_autoRaiseInFlight)
            {
                _autoRaiseApplied = true;
                _autoRaiseInFlight = true;
                try
                {
                    bool raised = await _gamingMode.AutoRaiseTargetAsync(_pid);
                    if (raised && RestoreButton != null)
                    {
                        RestoreButton.IsEnabled = true;
                        string note = $"   |   auto-raised to Above Normal (CPU-bound)";
                        if (CpuBoundText.Text != text + note) CpuBoundText.Text = text + note;
                    }
                }
                finally
                {
                    _autoRaiseInFlight = false;
                }
            }
        }

        private async void Action_ToggleGamingMode(object sender, RoutedEventArgs e)
        {
            GamingModeButton.IsEnabled = false;
            try
            {
                if (!_gamingMode.IsActive)
                {
                    var result = await _gamingMode.ActivateAsync(_pid);
                    GamingModeStatusText.Text = result.Summary;
                    if (result.Success)
                    {
                        GamingModeButton.Content = "Turn off Gaming mode";
                        RestoreButton.IsEnabled = true;
                    }
                }
                else
                {
                    _gamingMode.Deactivate();
                    GamingModeStatusText.Text = "Gaming mode off · priorities restored";
                    GamingModeButton.Content = "Enable Gaming mode";
                    RestoreButton.IsEnabled = false;
                }
            }
            catch (Exception ex)
            {
                GamingModeStatusText.Text = $"Gaming mode error: {ex.Message}";
            }
            finally
            {
                GamingModeButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Manual restore: reverts everything stellarisKIT changed (gaming mode
        /// lowering and/or the CPU-bound auto-raise), then resets detection so
        /// a fresh episode must build up again.
        /// </summary>
        private void Action_RestorePriorities(object sender, RoutedEventArgs e)
        {
            _gamingMode.Deactivate();
            _autoRaiseApplied = false;
            _cpuDetector.Reset();
            _lastProcessTotalTicks.Remove(_pid);

            GamingModeStatusText.Text = "Priorities restored";
            GamingModeButton.Content = "Enable Gaming mode";
            RestoreButton.IsEnabled = false;
        }

        private static long? TryGetSnapshotStart(SnapshotProcess? snapshot, int tid)
        {
            if (snapshot == null)
            {
                return null;
            }

            foreach (var thread in snapshot.Threads)
            {
                if (thread.ThreadId == (uint)tid && thread.StartAddress != 0)
                {
                    return thread.StartAddress;
                }
            }

            return null;
        }

        /// <summary>
        /// Resolves the Description column. Only the seven known DWM roles
        /// are ever labeled; anything else stays "(unnamed)". Sources, in
        /// order: the OS thread name (SetThreadDescription), then a
        /// build-independent DbgHelp symbol keyword match. Raw symbols and
        /// module+offset fallbacks are deliberately NOT shown — per spec,
        /// non-role threads must read as unnamed, matching System Informer.
        /// </summary>
        private string ResolveThreadDescription(int tid, Process proc, long startAddress)
        {
            string? explicitName = NativeSnapshotService.TryGetThreadDescription((uint)tid);
            if (!string.IsNullOrWhiteSpace(explicitName))
            {
                return explicitName;
            }

            if (startAddress != 0)
            {
                string? rawSymbol = _symbols.ResolveRaw(proc, startAddress);
                if (!string.IsNullOrWhiteSpace(rawSymbol))
                {
                    string? role = ThreadSymbolResolver.ClassifyDwmSymbol(rawSymbol);
                    if (!string.IsNullOrWhiteSpace(role))
                    {
                        return role;
                    }
                }
            }

            return string.Equals(proc.ProcessName, "dwm", StringComparison.OrdinalIgnoreCase)
                ? "(unnamed DWM thread)"
                : "(unnamed)";
        }

        /// <summary>
        /// Pinning predicate for the grid sort: only the seven known DWM
        /// roles (or a real OS thread name) count as named. Must stay in
        /// sync with the "(unnamed)" fallbacks above.
        /// </summary>
        private static bool IsUnnamed(string description) =>
            string.IsNullOrWhiteSpace(description)
            || string.Equals(description, "(unnamed)", StringComparison.Ordinal)
            || string.Equals(description, "(unnamed DWM thread)", StringComparison.Ordinal);

        /// <summary>
        /// Formats the thread's priority level the way System Informer does:
        /// "Time critical" (not "TimeCritical"), "Below normal", and
        /// "Custom (3)" for values outside the named enum instead of a bare
        /// "3" that looks like missing data. Shared with the rule editor.
        /// </summary>
        private static string FormatRelative(int level) =>
            stellarisKIT.Services.ThreadQueryService.FormatRelative(level);

        private static long GetThreadCycleTime(int tid)
        {
            foreach (NativeMethods.ThreadAccess access in new[]
            {
                NativeMethods.ThreadAccess.QueryLimitedInformation,
                NativeMethods.ThreadAccess.QueryInformation,
            })
            {
                using var handle = NativeMethods.Handles.OpenThread(access, false, (uint)tid);
                if (!handle.IsInvalid)
                {
                    int result = NativeMethods.Times.QueryThreadCycleTime(handle, out ulong cycles);
                    if (result != 0)
                    {
                        return (long)cycles;
                    }
                }
            }

            return 0;
        }

        private async void Action_Suspend(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ThreadDiagnosticRow row)
                await App.Current.ProcessTuning.SuspendThreadAsync(row.Tid);
        }

        private async void Action_Resume(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ThreadDiagnosticRow row)
                await App.Current.ProcessTuning.ResumeThreadAsync(row.Tid);
        }

        private async void Action_Efficiency(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string eco } fe && fe.DataContext is ThreadDiagnosticRow row)
            {
                await App.Current.ThreadTuning.SetEfficiencyAsync((uint)row.Tid, eco == "Enabled");
                RefreshThreadSettings(row);
            }
        }

        private async void Action_Boost(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string tag } fe && fe.DataContext is ThreadDiagnosticRow row)
            {
                await App.Current.ThreadTuning.SetBoostAsync((uint)row.Tid, tag == "Enabled");
                RefreshThreadSettings(row);
            }
        }

        private async void Action_Affinity(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ThreadDiagnosticRow row)
            {
                await ShowThreadAffinityAsync(fe, row);
            }
        }

        private void ThreadMenu_Opening(object sender, object e)
        {
            if (sender is MenuFlyout menu && menu.Target is FrameworkElement target && target.DataContext is ThreadDiagnosticRow row)
            {
                _ = UpdateThreadMenuStateAsync(menu, row);
            }
        }

        private async Task UpdateThreadMenuStateAsync(MenuFlyout menu, ThreadDiagnosticRow row)
        {
            // The submenu labels are intentionally updated when the menu opens,
            // not only after an action. This makes the current state visible
            // before changing anything.
            var priority = menu.Items.OfType<MenuFlyoutSubItem>()
                .FirstOrDefault(x => x.Text.StartsWith("Set Priority", StringComparison.OrdinalIgnoreCase));
            if (priority != null)
            {
                priority.Text = $"Set Priority  (current: {row.Relative})";
                MarkThreadMenuChoice(priority, row.Relative);
            }

            try
            {
                string affinity = await App.Current.ThreadTuning.GetAffinityDescriptionAsync((uint)row.Tid);
                row.AffinityText = affinity;
                var affinityItem = menu.Items.OfType<MenuFlyoutItem>()
                    .FirstOrDefault(x => x.Text.StartsWith("CPU affinity", StringComparison.OrdinalIgnoreCase));
                if (affinityItem != null)
                    affinityItem.Text = $"CPU affinity... (current: {affinity})";
            }
            catch { }

            try
            {
                bool boostEnabled = await App.Current.ThreadTuning.GetBoostAsync((uint)row.Tid);
                var boost = menu.Items.OfType<MenuFlyoutSubItem>()
                    .FirstOrDefault(x => x.Text.StartsWith("Priority boost", StringComparison.OrdinalIgnoreCase));
                if (boost != null)
                {
                    boost.Text = $"Priority boost  (current: {(boostEnabled ? "Enabled" : "Disabled")})";
                    MarkThreadMenuChoice(boost, boostEnabled ? "Enabled" : "Disabled");
                }
                row.BoostText = boostEnabled ? "Enabled" : "Disabled";
            }
            catch { }

            try
            {
                bool efficiencyEnabled = await App.Current.ThreadTuning.GetEfficiencyAsync((uint)row.Tid);
                var efficiency = menu.Items.OfType<MenuFlyoutSubItem>()
                    .FirstOrDefault(x => x.Text.StartsWith("Efficiency mode", StringComparison.OrdinalIgnoreCase));
                if (efficiency != null)
                {
                    efficiency.Text = $"Efficiency mode  (current: {(efficiencyEnabled ? "Enabled" : "Disabled")})";
                    MarkThreadMenuChoice(efficiency, efficiencyEnabled ? "Enabled" : "Disabled");
                }
                row.EfficiencyText = efficiencyEnabled ? "Enabled" : "Disabled";
            }
            catch { }
        }

        private static void MarkThreadMenuChoice(MenuFlyoutSubItem menu, string current)
        {
            foreach (var item in menu.Items.OfType<MenuFlyoutItem>())
            {
                string label = item.Tag?.ToString() switch
                {
                    "TimeCritical" => "Time Critical",
                    "AboveNormal" => "Above Normal",
                    "BelowNormal" => "Below Normal",
                    "Enabled" => "Enabled",
                    "Disabled" => "Disabled",
                    "Highest" => "Highest",
                    "Normal" => "Normal",
                    "Lowest" => "Lowest",
                    "Idle" => "Idle",
                    _ => item.Text.Replace(" ✓", "")
                };
                item.Text = string.Equals(label, current, StringComparison.OrdinalIgnoreCase)
                    || (current.Contains(" ", StringComparison.Ordinal) && string.Equals(label, current, StringComparison.OrdinalIgnoreCase))
                    ? $"{label}  ✓"
                    : label;
            }
        }

        private async void Action_ShowSettings(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ThreadDiagnosticRow row)
            {
                await RefreshThreadSettingsAsync(row);
                await ShowThreadSettingsAsync(
                    $"TID {row.Tid}\nAffinity: {row.AffinityText}\nPriority boost: {row.BoostText}\nEfficiency mode: {row.EfficiencyText}");
            }
        }

        private async void RefreshThreadSettings(ThreadDiagnosticRow row) => await RefreshThreadSettingsAsync(row);

        private async Task RefreshThreadSettingsAsync(ThreadDiagnosticRow row)
        {
            try
            {
                row.AffinityText = $"{await App.Current.ThreadTuning.GetAffinityDescriptionAsync((uint)row.Tid)}";
            }
            catch { row.AffinityText = "Unavailable"; }

            try
            {
                row.BoostText = (await App.Current.ThreadTuning.GetBoostAsync((uint)row.Tid)) ? "Enabled" : "Disabled";
            }
            catch { row.BoostText = "Unavailable"; }

            try
            {
                row.EfficiencyText = (await App.Current.ThreadTuning.GetEfficiencyAsync((uint)row.Tid)) ? "Enabled" : "Disabled";
            }
            catch { row.EfficiencyText = "Unavailable"; }
        }

        private async Task ShowThreadAffinityAsync(FrameworkElement anchor, ThreadDiagnosticRow row)
        {
            try
            {
                var currentState = await App.Current.ThreadTuning.GetAffinityStateAsync((uint)row.Tid);
                ulong current = currentState.Mask;
                ushort group = currentState.Group;
                ushort cpuCount = NativeMethods.Affinity.GetActiveProcessorCount(group);
                var panel = new StackPanel { Spacing = 4 };
                var boxes = new List<CheckBox>();

                for (int cpu = 0; cpu < cpuCount; cpu++)
                {
                    var box = new CheckBox
                    {
                        Content = $"Group {group} · CPU {cpu}",
                        IsChecked = (current & (1UL << cpu)) != 0,
                        Tag = cpu,
                    };
                    boxes.Add(box);
                    panel.Children.Add(box);
                }

                var dialog = new ContentDialog
                {
                    Title = $"CPU affinity — TID {row.Tid}",
                    Content = new ScrollViewer { Content = panel, MaxHeight = 420 },
                    PrimaryButtonText = "Apply",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = RootGrid.XamlRoot,
                };

                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }

                ulong mask = 0;
                foreach (var box in boxes)
                {
                    if (box.IsChecked == true && box.Tag is int cpu)
                    {
                        mask |= 1UL << cpu;
                    }
                }

                if (mask == 0)
                {
                    await ShowThreadErrorAsync("CPU affinity", new InvalidOperationException("Select at least one CPU."));
                    return;
                }

                await ApplyThreadAffinityAsync(row.Tid, group, mask);
            }
            catch (Exception ex)
            {
                await ShowThreadErrorAsync("CPU affinity", ex);
            }
        }

        private async Task ApplyThreadAffinityAsync(int tid, ushort group, ulong mask)
        {
            try
            {
                await App.Current.ThreadTuning.SetAffinityAsync((uint)tid, group, mask);
                var updated = Threads.FirstOrDefault(t => t.Tid == tid);
                if (updated != null) RefreshThreadSettings(updated);
            }
            catch (Exception ex)
            {
                await ShowThreadErrorAsync("CPU affinity", ex);
            }
        }

        private async Task ShowThreadSettingsAsync(string message)
        {
            try
            {
                await new ContentDialog
                {
                    Title = "Current thread settings",
                    Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    CloseButtonText = "Close",
                    XamlRoot = Content.XamlRoot,
                }.ShowAsync();
            }
            catch { }
        }

        private async Task ShowThreadErrorAsync(string title, Exception ex)
        {
            try
            {
                await new ContentDialog
                {
                    Title = $"{title} failed",
                    Content = new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap },
                    CloseButtonText = "Close",
                    XamlRoot = Content.XamlRoot,
                }.ShowAsync();
            }
            catch { }
        }

        private async void Action_Priority(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string p } fe && fe.DataContext is ThreadDiagnosticRow row)
            {
                var priority = p switch
                {
                    "TimeCritical" => TunerThreadPriority.TimeCritical,
                    "Highest" => TunerThreadPriority.Highest,
                    "AboveNormal" => TunerThreadPriority.AboveNormal,
                    "Normal" => TunerThreadPriority.Normal,
                    "BelowNormal" => TunerThreadPriority.BelowNormal,
                    "Lowest" => TunerThreadPriority.Lowest,
                    "Idle" => TunerThreadPriority.Idle,
                    _ => TunerThreadPriority.Normal
                };
                await App.Current.ProcessTuning.SetThreadPriorityAsync(row.Tid, priority);
            }
        }

        private void ThreadsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ThreadsList.SelectedItem is ThreadDiagnosticRow row)
            {
                RefreshThreadSettings(row);
                SelectedStartAddressText.Text = row.StartAddress;
                SelectedStateText.Text = $"State      {row.State}";
                try
                {
                    using var proc = Process.GetProcessById(_pid);
                    foreach (ProcessThread t in proc.Threads)
                    {
                        if (t.Id == row.Tid)
                        {
                                SelectedStartedText.Text = $"Started    {t.StartTime.ToString("h:mm:ss tt")}";
                                SelectedKernelUserText.Text = $"Kernel    {t.PrivilegedProcessorTime.ToString(@"m\:ss\.fff")}    User       {t.UserProcessorTime.ToString(@"m\:ss\.fff")}";
                                break;
                            }
                        }
                    }
                    catch 
                    { 
                        SelectedStartedText.Text = "Started    Unknown";
                        SelectedKernelUserText.Text = "Kernel    -    User       -";
                    }
                    
                    SelectedDescText.Text = $"Description {row.Description}";
                    SelectedSwitchesText.Text = $"Switches  {row.SwitchesText}";
                    SelectedCyclesText.Text = $"Cycles    {row.CyclesText}";
                    SelectedRelativeText.Text = $"Relative   {row.Relative}";
                    SelectedBaseText.Text = $"Base       {row.Base}    Dynamic    {row.Dynamic}";
                    SelectedAffinityText.Text = $"Affinity   {row.AffinityText}";
                    SelectedBoostText.Text = $"Boost      {row.BoostText}";
                    SelectedEfficiencyText.Text = $"Efficiency {row.EfficiencyText}";
                    
                    try
                    {
                        using var proc = Process.GetProcessById(_pid);
                        long searchAddr = row.StartAddressValue;
                        SelectedPhysicalPathText.Text = "Unknown location";
                        
                        foreach (ProcessModule mod in proc.Modules)
                        {
                            long baseAddr = mod.BaseAddress.ToInt64();
                            long endAddr = baseAddr + mod.ModuleMemorySize;
                            if (searchAddr >= baseAddr && searchAddr < endAddr)
                            {
                                SelectedPhysicalPathText.Text = mod.FileName;
                                break;
                            }
                        }
                    }
                    catch { }
                }
            }
            
            private string GetThreadState(ProcessThread t)
            {
                var state = t.ThreadState.ToString();
                if (t.ThreadState == System.Diagnostics.ThreadState.Wait)
                    state += $":{t.WaitReason}";
                return state;
            }
            
        /// <summary>
        /// Resolves a thread's start address to "module+offset". The BCL's
        /// ProcessThread.StartAddress is the loader entry (ntdll!RtlUserThreadStart)
        /// for EVERY thread, so the real per-thread entry point comes from the
        /// native snapshot (TEB start address) instead.
        /// </summary>
        private string GetStartAddress(Process proc, ProcessThread t, long snapshotStart)
        {
            long tAddr = snapshotStart;
            if (tAddr == 0)
            {
                try
                {
                    tAddr = t.StartAddress.ToInt64();
                }
                catch
                {
                    return "-";
                }
            }

            string res = $"0x{tAddr:X}";
            try
            {
                foreach (ProcessModule mod in proc.Modules)
                {
                    long bAddr = mod.BaseAddress.ToInt64();
                    long eAddr = bAddr + mod.ModuleMemorySize;
                    if (tAddr >= bAddr && tAddr < eAddr)
                    {
                        long offset = tAddr - bAddr;
                        res = $"{mod.ModuleName}+0x{offset:X}";
                        break;
                    }
                }
            }
            catch { }
            return res;
        }

            private void Action_Refresh(object sender, RoutedEventArgs e)
            {
                RefreshThreads();
            }

        ~ThreadDiagnosticsWindow()
        {
            _symbols.Dispose();
        }
    }
}
