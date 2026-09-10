using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using stellarisKIT.Models;

namespace stellarisKIT.Services;

/// <summary>Outcome of one apply pass over a single process.</summary>
public sealed class RuleApplyResult
{
    public bool ProcessFound { get; init; }
    public int Attempted { get; init; }
    public int Succeeded { get; init; }
    public DateTime Time { get; init; } = DateTime.Now;
    /// <summary>Live matched-thread count per thread rule, in rule order.</summary>
    public List<int> ThreadTargets { get; init; } = new();
}

/// <summary>
/// Background WMI watcher for Win32_ProcessStartTrace that intercepts process launches
/// and maps them against saved JSON rules, applying Priority, Boost, Efficiency,
/// Affinity, CPU Sets and per-thread priorities. Every action is applied and
/// verified independently; the outcome lands in <c>TunerProfile.LastResult</c>.
/// Mutations of bound profiles always happen on the UI dispatcher.
/// </summary>
public sealed class ProfileWatcherService : IDisposable
{
    private readonly ProcessTuningService _tuning;
    private readonly CpuSetService _cpuSets;
    private readonly ThreadTuningService _threads;
    private readonly DispatcherQueue? _dispatcher;
    private readonly object _lock = new();
    private ManagementEventWatcher? _watcher;
    private string _profilePath;
    private string _logPath;

    /// <summary>
    /// PIDs this watcher auto-boosted via Gaming mode rules (launch events).
    /// When the LAST one exits, Gaming mode is deactivated so the machine
    /// never stays lowered while nothing game-related is running.
    /// </summary>
    private readonly HashSet<int> _gamingModePids = new();
    private readonly object _gamingLock = new();
    private System.Threading.Timer? _gamingLivenessTimer;

    public List<TunerProfile> ActiveProfiles { get; private set; } = new();

    /// <summary>Raised on the UI thread after rules change or an apply pass updates results.</summary>
    public event EventHandler? RulesChanged;

    public ProfileWatcherService(ProcessTuningService tuning, CpuSetService cpuSets, ThreadTuningService threads, string? profilePath = null)
    {
        _tuning = tuning;
        _cpuSets = cpuSets;
        _threads = threads;
        try
        {
            _dispatcher = DispatcherQueue.GetForCurrentThread();
        }
        catch
        {
            _dispatcher = null;
        }
        _profilePath = profilePath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "stellarisKIT", "threadtuner-profiles.json");
        _logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "stellarisKIT", "rules-debug.log");
    }

    /// <summary>Temporary diagnostics for the "rules don't stick" report. Appends, never throws.</summary>
    internal void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            try
            {
                if (new FileInfo(_logPath).Length > 200 * 1024)
                {
                    File.Delete(_logPath);
                }
            }
            catch
            {
            }
            File.AppendAllText(_logPath,
                $"{DateTime.Now:HH:mm:ss.fff} [pid={Environment.ProcessId}] {message}\r\n");
        }
        catch
        {
        }
    }

    public async Task LoadProfilesAsync()
    {
        try
        {
            if (File.Exists(_profilePath))
            {
                var text = await File.ReadAllTextAsync(_profilePath);
                Log($"load: {text.Length} bytes from {_profilePath}");
                var loaded = JsonSerializer.Deserialize<List<TunerProfile>>(text);
                Log($"load: deserialized {(loaded == null ? "null" : loaded.Count.ToString())} profiles");
                if (loaded != null)
                {
                    lock (_lock)
                    {
                        ActiveProfiles = loaded;
                    }
                }
            }
            else
            {
                Log("load: no file yet");
            }
        }
        catch (Exception ex)
        {
            Log("load FAILED: " + ex.GetType().Name + ": " + ex.Message);
        }
        RaiseChanged();
    }

    /// <summary>
    /// Serializes profile saves. Concurrent saves previously raced on a shared
    /// .tmp filename — one save moved the file out from under the other,
    /// logging "save FAILED: tmp not found" and, in the worst interleaving,
    /// letting a stale/empty snapshot win. Evidence: rules-debug.log.
    /// </summary>
    private readonly System.Threading.SemaphoreSlim _saveLock = new(1, 1);

    public async Task<bool> SaveProfilesAsync()
    {
        try
        {
            List<TunerProfile> snapshot;
            lock (_lock)
            {
                snapshot = ActiveProfiles.ToList();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_profilePath)!);
            var text = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });

            await _saveLock.WaitAsync();
            try
            {
                // Atomic write: a crash mid-save must never leave a truncated
                // file. Unique tmp name per save — a shared name raced between
                // concurrent saves (one Move stole the other's tmp file).
                string tmp = _profilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllTextAsync(tmp, text);
                File.Move(tmp, _profilePath, overwrite: true);
            }
            finally
            {
                _saveLock.Release();
            }

            Log($"save ok: {snapshot.Count} profiles, {text.Length} bytes");
            return true;
        }
        catch (Exception ex)
        {
            Log("save FAILED: " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    public async Task<bool> AddOrUpdate(TunerProfile profile)
    {
        lock (_lock)
        {
            if (!ActiveProfiles.Contains(profile))
            {
                ActiveProfiles.Add(profile);
            }
        }
        Log($"add: pattern=[{profile.Pattern}] actions={profile.ProcessActionCount} threadrules={profile.ThreadRules?.Count ?? 0}");
        bool saved = await SaveProfilesAsync();
        RaiseChanged();

        // The rule set may now include a gaming-mode rule whose process is
        // ALREADY running (rule created/edited mid-game). Re-evaluate so it
        // arms without needing an app restart or a game relaunch.
        EvaluateGamingModeRules();
        return saved;
    }

    public async Task<bool> Remove(TunerProfile profile)
    {
        lock (_lock)
        {
            ActiveProfiles.Remove(profile);
        }
        Log($"remove: pattern=[{profile.Pattern}]");
        bool saved = await SaveProfilesAsync();
        RaiseChanged();
        EvaluateGamingModeRules();
        return saved;
    }

    public List<TunerProfile> FindMatches(string processName)
    {
        lock (_lock)
        {
            return ActiveProfiles
                .Where(p => p.Enabled && p.AutoApply && ProfileMatcher.Matches(p.Pattern, processName))
                .ToList();
        }
    }

    /// <summary>Applies one profile to one pid. Never mutates the profile; returns the outcome.</summary>
    public async Task<RuleApplyResult> ApplyToProcessAsync(TunerProfile profile, int pid)
    {
        int attempted = 0;
        int succeeded = 0;
        var targets = new List<int>();

        if (profile.PriorityClass.HasValue)
        {
            attempted++;
            try
            {
                await _tuning.SetPriorityAsync(pid, profile.PriorityClass.Value);
                succeeded++;
            }
            catch
            {
            }
        }

        if (profile.BoostEnabled.HasValue)
        {
            attempted++;
            try
            {
                await _tuning.SetBoostAsync(pid, profile.BoostEnabled.Value);
                succeeded++;
            }
            catch
            {
            }
        }

        if (profile.EfficiencyMode.HasValue)
        {
            attempted++;
            try
            {
                await _tuning.SetEfficiencyAsync(pid, profile.EfficiencyMode.Value);
                succeeded++;
            }
            catch
            {
            }
        }

        if (profile.AffinityMask.HasValue)
        {
            attempted++;
            try
            {
                await _tuning.SetAffinityAsync(pid, profile.AffinityMask.Value);
                succeeded++;
            }
            catch
            {
            }
        }

        if (profile.CpuSetIds != null && profile.CpuSetIds.Count > 0)
        {
            attempted++;
            try
            {
                await _cpuSets.SetProcessCpuSetsAsync(pid, profile.CpuSetIds);
                succeeded++;
            }
            catch
            {
            }
        }

        if (profile.ThreadRules != null && profile.ThreadRules.Count > 0)
        {
            var live = await ThreadQueryService.ListThreadsAsync(pid);
            foreach (var rule in profile.ThreadRules)
            {
                var matched = live.Where(t => ThreadMatches(rule, t)).ToList();
                targets.Add(matched.Count);
                foreach (var t in matched)
                {
                    attempted++;
                    try
                    {
                        await _threads.SetPriorityAsync((uint)t.Tid, rule.Priority);
                        succeeded++;
                    }
                    catch
                    {
                    }

                    if (rule.EfficiencyMode.HasValue)
                    {
                        attempted++;
                        try
                        {
                            await _threads.SetEfficiencyAsync((uint)t.Tid, rule.EfficiencyMode.Value);
                            succeeded++;
                        }
                        catch
                        {
                        }
                    }

                    if (rule.BoostEnabled.HasValue)
                    {
                        attempted++;
                        try
                        {
                            await _threads.SetBoostAsync((uint)t.Tid, rule.BoostEnabled.Value);
                            succeeded++;
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        return new RuleApplyResult
        {
            ProcessFound = true,
            Attempted = attempted,
            Succeeded = succeeded,
            ThreadTargets = targets,
        };
    }

    /// <summary>Applies a profile to every currently-running matching process, updates LastResult.</summary>
    public async Task<RuleApplyResult> ApplyProfileNowAsync(TunerProfile profile)
    {
        var pids = FindPids(profile.Pattern);
        Log($"apply-now: pattern=[{profile.Pattern}] pids=[{string.Join(",", pids)}]");
        if (pids.Count == 0)
        {
            profile.LastResult = "Waiting for process";
            profile.RefreshSummaries();
            await SaveProfilesAsync();
            RaiseChanged();
            return new RuleApplyResult { ProcessFound = false };
        }

        int attempted = 0;
        int succeeded = 0;
        List<int> targets = new();
        foreach (int pid in pids)
        {
            var r = await ApplyToProcessAsync(profile, pid);
            attempted += r.Attempted;
            succeeded += r.Succeeded;
            if (targets.Count == 0)
            {
                targets = r.ThreadTargets;
            }
        }

        CommitResult(profile, attempted, succeeded, targets);
        bool tracked;
        lock (_lock)
        {
            tracked = ActiveProfiles.Contains(profile);
        }
        if (tracked)
        {
            await SaveProfilesAsync();
        }
        else
        {
            // A detached profile must never trigger a save: persisting here
            // would write an empty/stale list over real rules. This exact
            // clobber once wiped a live rules file via a test harness.
            Log($"apply-now: pattern=[{profile.Pattern}] not in store, save skipped");
        }
        RaiseChanged();
        return new RuleApplyResult
        {
            ProcessFound = true,
            Attempted = attempted,
            Succeeded = succeeded,
            ThreadTargets = targets,
        };
    }

    public static bool ThreadMatches(TunerThreadRule rule, LiveThreadInfo thread)
    {
        bool descOk = string.IsNullOrWhiteSpace(rule.Description)
            || string.Equals(thread.Description, rule.Description, StringComparison.OrdinalIgnoreCase);
        bool startOk = string.IsNullOrWhiteSpace(rule.StartAddress)
            || string.Equals(thread.StartAddress, rule.StartAddress, StringComparison.OrdinalIgnoreCase);
        return descOk && startOk && (!string.IsNullOrWhiteSpace(rule.Description) || !string.IsNullOrWhiteSpace(rule.StartAddress));
    }

    public static void CommitResult(TunerProfile profile, int attempted, int succeeded, List<int> targets)
    {
        profile.LastResult = attempted == 0
            ? "No actions defined"
            : $"{succeeded}/{attempted} actions applied · {DateTime.Now:HH:mm:ss}";
        if (profile.ThreadRules != null)
        {
            for (int i = 0; i < profile.ThreadRules.Count && i < targets.Count; i++)
            {
                profile.ThreadRules[i].TargetCount = targets[i];
            }
        }
        profile.RefreshSummaries();
    }

    public static List<int> FindPids(string pattern)
    {
        var pids = new List<int>();
        if (string.IsNullOrWhiteSpace(pattern)) return pids;
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (ProfileMatcher.Matches(pattern, proc.ProcessName))
                    {
                        pids.Add(proc.Id);
                    }
                }
                catch
                {
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch
        {
        }
        return pids;
    }

    public void StartWatcher()
    {
        StopWatcher();
        try
        {
            _watcher = new ManagementEventWatcher(new EventQuery("SELECT * FROM Win32_ProcessStartTrace"));
            _watcher.EventArrived += OnProcessStarted;
            _watcher.Start();
        }
        catch
        {
            _watcher = null;
        }
    }

    public void StopWatcher()
    {
        if (_watcher != null)
        {
            try
            {
                _watcher.Stop();
                _watcher.EventArrived -= OnProcessStarted;
                _watcher.Dispose();
            }
            catch
            {
            }
            _watcher = null;
        }
    }

    private async void OnProcessStarted(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var name = e.NewEvent.Properties["ProcessName"]?.Value as string;
            var pidProp = e.NewEvent.Properties["ProcessID"]?.Value;
            if (string.IsNullOrEmpty(name) || pidProp == null) return;

            int pid = Convert.ToInt32(pidProp);

            // Wait an extremely brief moment to allow the process core initialization,
            // while making the tuning application feel absolutely instantaneous.
            await Task.Delay(100);

            var matched = FindMatches(name);

            // Automatic Gaming mode: any enabled rule with GamingModeAuto
            // matching this launch arms app-wide Gaming mode. The exit watcher
            // deactivates it when the last armed process is gone.
            bool gamingRule = matched.Any(p => p.GamingModeAuto);
            if (gamingRule)
            {
                // Prune armed pids whose processes are already gone (a missed
                // stop event must not keep Gaming mode on forever).
                List<int> armed;
                lock (_gamingLock)
                {
                    _gamingModePids.RemoveWhere(p =>
                    {
                        try { Process.GetProcessById(p).Dispose(); return false; }
                        catch { return true; }
                    });
                    _gamingModePids.Add(pid);
                    armed = _gamingModePids.ToList();
                }

                StartGamingModeExitWatcher();
                StartGamingModeLivenessMonitor();
                if (!App.Current.GamingMode.IsActive)
                {
                    _watcherInitiatedGamingMode = true;
                    await App.Current.GamingMode.ActivateAsync(pid, armed);
                    Log($"gaming-mode: activated for [{name}] pid={pid}, protected={armed.Count}");
                }
            }

            if (matched.Count == 0) return;

            foreach (var profile in matched)
            {
                var result = await ApplyToProcessAsync(profile, pid);
                if (_dispatcher != null)
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        CommitResult(profile, result.Attempted, result.Succeeded, result.ThreadTargets);
                        _ = SaveProfilesAsync();
                        RaiseChanged();
                    });
                }
            }
        }
        catch
        {
            // Avoid failing WMI background pump due to process protection access denies
        }
    }

    /// <summary>
    /// True when the CURRENT Gaming mode session was started by the watcher
    /// (automatic). Manual sessions from the UI are never auto-deactivated —
    /// the user controls those via the toggle/Restore buttons.
    /// </summary>
    private bool _watcherInitiatedGamingMode;

    /// <summary>
    /// Initial sweep at startup: arm Gaming mode for any gaming-mode rule
    /// whose process is ALREADY running (app started mid-game), and start the
    /// process-exit watcher that deactivates Gaming mode when the last armed
    /// process exits. Both are no-ops when no gaming-mode rules exist.
    /// </summary>
    public void StartGamingModeWatcher()
    {
        EvaluateGamingModeRules();
    }

    /// <summary>
    /// Re-evaluates gaming-mode rules against currently running processes:
    /// arms newly matching pids (rule created/edited mid-game, or app started
    /// mid-game), disarms pids no longer covered by any gaming rule, then
    /// activates or restores Gaming mode to match. Safe to call any time.
    /// </summary>
    public void EvaluateGamingModeRules()
    {
        try
        {
            List<TunerProfile> gamingRules;
            lock (_lock)
            {
                gamingRules = ActiveProfiles.Where(p => p.Enabled && p.GamingModeAuto).ToList();
            }

            var expected = new HashSet<int>();
            foreach (var profile in gamingRules)
            {
                foreach (int pid in FindPids(profile.Pattern))
                {
                    expected.Add(pid);
                }
            }

            lock (_gamingLock)
            {
                // Dead pids first (missed stop events)...
                _gamingModePids.RemoveWhere(p =>
                {
                    try { Process.GetProcessById(p).Dispose(); return false; }
                    catch { return true; }
                });
                // ...then pids whose rule disappeared or was edited to not match.
                _gamingModePids.RemoveWhere(p => !expected.Contains(p));
                _gamingModePids.UnionWith(expected);
            }

            List<int> armed;
            lock (_gamingLock)
            {
                armed = _gamingModePids.ToList();
            }

            if (armed.Count > 0)
            {
                StartGamingModeExitWatcher();
                StartGamingModeLivenessMonitor();
                if (!App.Current.GamingMode.IsActive)
                {
                    _watcherInitiatedGamingMode = true;
                    _ = App.Current.GamingMode.ActivateAsync(armed[0], armed).ContinueWith(_ =>
                        Log($"gaming-mode: evaluated + activated, armed pids=[{string.Join(",", armed)}]"));
                }
            }
            else if (App.Current.GamingMode.IsActive && _watcherInitiatedGamingMode)
            {
                _watcherInitiatedGamingMode = false;
                Log("gaming-mode: no armed processes remain (rules changed), restoring");
                App.Current.GamingMode.Deactivate();
            }
        }
        catch (Exception ex)
        {
            Log("gaming-mode evaluate FAILED: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private ManagementEventWatcher? _exitWatcher;

    /// <summary>
    /// WMI stop events are best effort. This independent timer checks the
    /// armed game PIDs every second, so closing a game always restores even
    /// when WMI drops or delays Win32_ProcessStopTrace.
    /// </summary>
    private void StartGamingModeLivenessMonitor()
    {
        _gamingLivenessTimer ??= new System.Threading.Timer(
            _ => CheckGamingProcessLiveness(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        // A previous game may have stopped the existing timer; restart it for
        // the next automatic Gaming mode session.
        _gamingLivenessTimer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void CheckGamingProcessLiveness()
    {
        try
        {
            bool hasArmed;
            bool anyAlive = false;
            lock (_gamingLock)
            {
                hasArmed = _gamingModePids.Count > 0;
                _gamingModePids.RemoveWhere(p =>
                {
                    try
                    {
                        using var process = Process.GetProcessById(p);
                        anyAlive = true;
                        return false;
                    }
                    catch
                    {
                        return true;
                    }
                });

                if (_gamingModePids.Count > 0)
                {
                    anyAlive = true;
                }
            }

            if (hasArmed && !anyAlive && _watcherInitiatedGamingMode)
            {
                _watcherInitiatedGamingMode = false;
                Log("gaming-mode: liveness monitor found no armed game processes, restoring priorities");
                App.Current.GamingMode.Deactivate();
                _gamingLivenessTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            }
        }
        catch
        {
            // The monitor must never terminate because one process disappeared.
        }
    }

    private void StartGamingModeExitWatcher()
    {
        if (_exitWatcher != null)
        {
            return;
        }

        try
        {
            _exitWatcher = new ManagementEventWatcher(new EventQuery("SELECT * FROM Win32_ProcessStopTrace"));
            _exitWatcher.EventArrived += OnProcessStopped;
            _exitWatcher.Start();
        }
        catch
        {
            _exitWatcher = null;
        }
    }

    public void StopGamingModeExitWatcher()
    {
        if (_exitWatcher != null)
        {
            try
            {
                _exitWatcher.Stop();
                _exitWatcher.EventArrived -= OnProcessStopped;
                _exitWatcher.Dispose();
            }
            catch
            {
            }
            _exitWatcher = null;
        }
    }

    private void OnProcessStopped(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var pidProp = e.NewEvent.Properties["ProcessID"]?.Value;
            if (pidProp == null) return;

            int pid = Convert.ToInt32(pidProp);
            bool wasLast;
            lock (_gamingLock)
            {
                if (!_gamingModePids.Remove(pid))
                {
                    return; // not a gaming-mode process
                }

                wasLast = _gamingModePids.Count == 0;
            }

            if (wasLast && _watcherInitiatedGamingMode)
            {
                _watcherInitiatedGamingMode = false;
                Log("gaming-mode: last armed process exited, restoring priorities");
                App.Current.GamingMode.Deactivate();
                _gamingLivenessTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            }
            else if (wasLast)
            {
                Log("gaming-mode: armed processes gone; manual Gaming mode session left untouched");
            }
        }
        catch
        {
            // Never let the WMI exit pump die.
        }
    }

    private void RaiseChanged()
    {
        if (_dispatcher != null)
        {
            _dispatcher.TryEnqueue(() => RulesChanged?.Invoke(this, EventArgs.Empty));
        }
        else
        {
            RulesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        StopWatcher();
        StopGamingModeExitWatcher();
        _gamingLivenessTimer?.Dispose();
        _gamingLivenessTimer = null;
    }
}
