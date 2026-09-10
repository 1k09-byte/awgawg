using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using stellarisKIT.Controls;
using stellarisKIT.Models;
using stellarisKIT.Services;
using stellarisKIT.ViewModels;
using System.Linq;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace stellarisKIT.Pages
{
    public sealed partial class ThreadTunerPage : Page
    {
        public ThreadTunerViewModel ViewModel { get; }

        private readonly stellarisKIT.Services.StartupService _startup = new();
        private bool _syncingStartupToggle;
        private List<stellarisKIT.Native.CpuSetEntry> _topologyCache = new();
        private TunerProcessRow? _contextProcessRow;

        public ThreadTunerPage()
        {
            this.InitializeComponent();
            this.ViewModel = new ThreadTunerViewModel();
            this.DataContext = ViewModel;
            
            // Clean unreferenced events and start background analytics tracking
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            this.Loaded += ThreadTunerPage_Loaded;
            this.Unloaded += ThreadTunerPage_Unloaded;
        }

        private void TabNav_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Microsoft.UI.Xaml.Controls.Button btn && btn.Tag is string tag)
            {
                if (tag == "Rules")
                {
                    ProcessesGrid.Visibility = Visibility.Collapsed;
                    ProcessesHeader.Visibility = Visibility.Collapsed;
                    ProcessesFooter.Visibility = Visibility.Collapsed;
                    ReservedCpuSetsFrame.Visibility = Visibility.Collapsed;
                    RulesPanel.Visibility = Visibility.Visible;
                    ViewModel.SyncProfiles();
                    RefreshRulesMeta();
                    
                    TabRules.Foreground = ThemeBrush("SystemAccentColorLight2");
                    TabRules.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
                    
                    TabProcesses.Foreground = ThemeBrush("TextFillColorSecondaryBrush");
                    TabProcesses.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                    
                    TabReservedCpuSets.Foreground = ThemeBrush("TextFillColorSecondaryBrush");
                    TabReservedCpuSets.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                }
                else if (tag == "ReservedCpuSets")
                {
                    ProcessesGrid.Visibility = Visibility.Collapsed;
                    ProcessesHeader.Visibility = Visibility.Collapsed;
                    ProcessesFooter.Visibility = Visibility.Collapsed;
                    RulesPanel.Visibility = Visibility.Collapsed;
                    ReservedCpuSetsFrame.Visibility = Visibility.Visible;
                    
                    if (ReservedCpuSetsFrame.Content == null)
                    {
                        ReservedCpuSetsFrame.Navigate(typeof(ReservedCpuSetsPage));
                    }
                    
                    TabReservedCpuSets.Foreground = ThemeBrush("SystemAccentColorLight2");
                    TabReservedCpuSets.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
                    
                    TabProcesses.Foreground = ThemeBrush("TextFillColorSecondaryBrush");
                    TabProcesses.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                    
                    TabRules.Foreground = ThemeBrush("TextFillColorSecondaryBrush");
                    TabRules.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                }
                else
                {
                    ProcessesGrid.Visibility = Visibility.Visible;
                    ProcessesHeader.Visibility = Visibility.Visible;
                    ProcessesFooter.Visibility = Visibility.Visible;
                    RulesPanel.Visibility = Visibility.Collapsed;
                    ReservedCpuSetsFrame.Visibility = Visibility.Collapsed;
                    
                    TabProcesses.Foreground = ThemeBrush("SystemAccentColorLight2");
                    TabProcesses.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
                    
                    TabRules.Foreground = ThemeBrush("TextFillColorSecondaryBrush");
                    TabRules.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                    
                    TabReservedCpuSets.Foreground = ThemeBrush("TextFillColorSecondaryBrush");
                    TabReservedCpuSets.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                }
            }
        }

        private async void ViewModel_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // Removed Dashboard binding logic as UI is full width list
        }

        private async void ThreadTunerPage_Loaded(object sender, RoutedEventArgs e)
        {
            // Fully migrating out Side-By-Side Topologies
            ViewModel.LoadProcessesCommand.Execute(null);
            ViewModel.SyncProfiles();
            RefreshRulesMeta();
            App.Current.ProfileWatcher.RulesChanged += (_, _) => RefreshRulesMeta();
            // Gaming mode can be toggled automatically by the watcher (rule
            // launch/exit); keep the page controls in sync when that happens.
            App.Current.ProfileWatcher.RulesChanged += (_, _) =>
            {
                RefreshPageGamingModeUi();
                PageGamingStatusText.Text = App.Current.GamingMode.IsActive
                    ? "Gaming mode ON (automatic · from a rule)"
                    : string.Empty;
            };
            _ = SyncStartupToggleAsync();
        }

        private async Task SyncStartupToggleAsync()
        {
            _syncingStartupToggle = true;
            try
            {
                StartupToggle.IsOn = await _startup.IsEnabledAsync();
            }
            catch
            {
                StartupToggle.IsOn = false;
            }
            finally
            {
                _syncingStartupToggle = false;
            }
        }

        private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncingStartupToggle) return;
            _syncingStartupToggle = true;
            try
            {
                bool ok = await _startup.SetEnabledAsync(StartupToggle.IsOn);
                if (!ok)
                {
                    StartupToggle.IsOn = !StartupToggle.IsOn;
                }
            }
            catch
            {
                StartupToggle.IsOn = !StartupToggle.IsOn;
            }
            finally
            {
                _syncingStartupToggle = false;
            }
        }

        private void ThreadTunerPage_Unloaded(object sender, RoutedEventArgs e)
        {
            // Pause ViewModel timer on unload
        }

        /// <summary>
        /// Resolves a theme resource to a brush. Theme accent keys such as
        /// "SystemAccentColorLight2" hold a <see cref="Windows.UI.Color"/>, not a
        /// Brush, so a direct cast crashes the moment a tab is clicked. Falls
        /// back to white rather than throwing when the key is missing.
        /// </summary>
        private static Microsoft.UI.Xaml.Media.Brush ThemeBrush(string key)
        {
            if (Application.Current.Resources.TryGetValue(key, out object? value))
            {
                if (value is Microsoft.UI.Xaml.Media.Brush brush)
                {
                    return brush;
                }

                if (value is Windows.UI.Color color)
                {
                    return new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
                }
            }

            return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
        }

        private TunerProcessRow? GetRow(object sender) => (sender as FrameworkElement)?.DataContext as TunerProcessRow;

        // ---- Gaming mode (main page) -------------------------------
        // Shares the app-wide GamingModeService with the Threads window:
        // one restore map, so the two UIs can never fight over priorities.

        private TunerProcessRow? SelectedProcess =>
            (ProcessesGrid.SelectedItem as TunerProcessRow)
            ?? (ProcessesGrid.ItemsSource as System.Collections.Generic.IEnumerable<TunerProcessRow>)?.FirstOrDefault(p => p.Pid == _lastSelectedPid);

        private int _lastSelectedPid = -1;

        private void ProcessesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ProcessesGrid.SelectedItem is TunerProcessRow row)
            {
                _lastSelectedPid = row.Pid;
            }

            RefreshPageGamingModeUi();
        }

        /// <summary>Syncs the page Gaming mode controls with the shared service state.</summary>
        private void RefreshPageGamingModeUi()
        {
            var svc = App.Current.GamingMode;
            bool hasTarget = SelectedProcess is not null;

            PageGamingModeButton.Content = svc.IsActive ? "Turn off Gaming mode" : "Enable Gaming mode";
            // Stays clickable even without a selection: clicking then explains
            // what to do, instead of a greyed-out button that silently ignores
            // clicks (the "nothing happened" failure mode).
            PageGamingModeButton.IsEnabled = true;
            PageRestoreButton.IsEnabled = svc.RestorableCount > 0;

            string target = SelectedProcess is { } r ? r.Name : "(no process selected)";
            PageGamingStatusText.Text = svc.IsActive
                ? $"Gaming mode ON · target {target} · {svc.RestorableCount} restorable"
                : hasTarget
                    ? $"Ready · target {target}"
                    : "Select a process row, or use a rule with Automatic Gaming mode";
        }

        private async void Page_ToggleGamingMode(object sender, RoutedEventArgs e)
        {
            var svc = App.Current.GamingMode;
            PageGamingModeButton.IsEnabled = false;
            try
            {
                if (!svc.IsActive)
                {
                    if (SelectedProcess is not { } row)
                    {
                        PageGamingStatusText.Text = "Click a process row first, or create a rule with 'Automatic Gaming mode' ticked";
                        return;
                    }

                    var result = await svc.ActivateAsync(row.Pid);
                    PageGamingStatusText.Text = result.Summary;
                }
                else
                {
                    svc.Deactivate();
                    PageGamingStatusText.Text = "Gaming mode off · priorities restored";
                }
            }
            catch (Exception ex)
            {
                PageGamingStatusText.Text = $"Gaming mode error: {ex.Message}";
            }
            finally
            {
                RefreshPageGamingModeUi();
            }
        }

        private void Page_RestorePriorities(object sender, RoutedEventArgs e)
        {
            App.Current.GamingMode.Deactivate();
            PageGamingStatusText.Text = "Priorities restored";
            RefreshPageGamingModeUi();
        }

        private async void ExecuteTuning(object sender, Func<TunerProcessRow, Task> action)
        {
            if (GetRow(sender) is { } row)
            {
                try
                {
                    await action(row);
                }
                catch (Exception ex)
                {
                    _ = new ContentDialog
                    {
                        Title = "Execution Blocked",
                        Content = new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap },
                        CloseButtonText = "Close",
                        XamlRoot = this.XamlRoot
                    }.ShowAsync();
                }
            }
        }

        private void ProcessMenu_Opening(object sender, object e)
        {
            if (sender is MenuFlyout menu && menu.Target is FrameworkElement target && target.DataContext is TunerProcessRow row)
            {
                // MenuFlyoutItem does not reliably inherit the row DataContext
                // on every WinUI version. Keep the target captured so actions
                // still work when invoked from the context menu.
                _contextProcessRow = row;
                _ = UpdateProcessMenuStateAsync(menu, row);
            }
        }

        private TunerProcessRow? GetContextProcessRow(object sender) =>
            GetRow(sender) ?? _contextProcessRow;

        private async Task UpdateProcessMenuStateAsync(MenuFlyout menu, TunerProcessRow row)
        {
            // Update the labels before the user chooses an action.  The row is
            // populated from the same native reads, but refresh the expensive
            // flags here so the menu always describes the live process.
            var priority = menu.Items.OfType<MenuFlyoutSubItem>()
                .FirstOrDefault(x => x.Text.StartsWith("Priority", StringComparison.OrdinalIgnoreCase));
            if (priority != null)
            {
                priority.Text = $"Priority  (current: {row.PriorityText})";
                MarkMenuChoice(priority, row.PriorityText);
            }

            var affinity = menu.Items.OfType<MenuFlyoutItem>()
                .FirstOrDefault(x => x.Text.StartsWith("CPU affinity", StringComparison.OrdinalIgnoreCase));
            if (affinity != null)
            {
                affinity.Text = $"CPU affinity... (current: {row.AffinitySummary})";
            }

            try
            {
                bool boostEnabled = await App.Current.ProcessTuning.GetBoostAsync(row.Pid);
                var boost = menu.Items.OfType<MenuFlyoutSubItem>()
                    .FirstOrDefault(x => x.Text.StartsWith("Priority boost", StringComparison.OrdinalIgnoreCase));
                if (boost != null)
                {
                    boost.Text = $"Priority boost  (current: {(boostEnabled ? "Enabled" : "Disabled")})";
                    MarkMenuChoice(boost, boostEnabled ? "Enabled" : "Disabled");
                }
            }
            catch { }

            try
            {
                bool efficiencyEnabled = await App.Current.ProcessTuning.GetEfficiencyAsync(row.Pid);
                var efficiency = menu.Items.OfType<MenuFlyoutSubItem>()
                    .FirstOrDefault(x => x.Text.StartsWith("Efficiency mode", StringComparison.OrdinalIgnoreCase));
                if (efficiency != null)
                {
                    efficiency.Text = $"Efficiency mode  (current: {(efficiencyEnabled ? "Enabled" : "Disabled")})";
                    MarkMenuChoice(efficiency, efficiencyEnabled ? "Enabled" : "Disabled");
                }
            }
            catch { }
        }

        private static void MarkMenuChoice(MenuFlyoutSubItem menu, string current)
        {
            foreach (var item in menu.Items.OfType<MenuFlyoutItem>())
            {
                string label = item.Tag?.ToString() switch
                {
                    "AboveNormal" => "Above normal",
                    "BelowNormal" => "Below normal",
                    _ => item.Tag?.ToString() ?? item.Text.Replace(" ✓", "")
                };
                item.Text = string.Equals(label, current, StringComparison.OrdinalIgnoreCase)
                    ? $"{label}  ✓"
                    : label;
            }
        }

        private async void Action_ShowSettings(object sender, RoutedEventArgs e)
        {
            if (GetContextProcessRow(sender) is not { } row) return;

            try
            {
                ulong affinity = await App.Current.ProcessTuning.GetAffinityAsync(row.Pid);
                bool boost = await App.Current.ProcessTuning.GetBoostAsync(row.Pid);
                bool efficiency = await App.Current.ProcessTuning.GetEfficiencyAsync(row.Pid);

                await new ContentDialog
                {
                    Title = $"Current settings — {row.Name}",
                    Content = new TextBlock
                    {
                        Text = $"PID: {row.Pid}\nAffinity mask: 0x{affinity:X}\nCPUs: {FormatCpuMask(affinity)}\nPriority boost: {(boost ? "Enabled" : "Disabled")}\nEfficiency mode: {(efficiency ? "Enabled" : "Disabled")}",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    CloseButtonText = "Close",
                    XamlRoot = this.XamlRoot,
                }.ShowAsync();
            }
            catch (Exception ex)
            {
                await new ContentDialog
                {
                    Title = "Could not read settings",
                    Content = new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap },
                    CloseButtonText = "Close",
                    XamlRoot = this.XamlRoot,
                }.ShowAsync();
            }
        }

        private static string FormatCpuMask(ulong mask)
        {
            var cpus = new List<string>();
            for (int i = 0; i < 64; i++)
            {
                if ((mask & (1UL << i)) != 0) cpus.Add(i.ToString());
            }
            return cpus.Count == 0 ? "None" : string.Join(", ", cpus);
        }

        private void Action_Terminate(object sender, RoutedEventArgs e) => ExecuteTuning(sender, row => App.Current.ProcessTuning.TerminateProcessAsync(row.Pid));
        private void Action_Suspend(object sender, RoutedEventArgs e) => ExecuteTuning(sender, row => App.Current.ProcessTuning.SuspendProcessAsync(row.Pid));
        private void Action_Resume(object sender, RoutedEventArgs e) => ExecuteTuning(sender, row => App.Current.ProcessTuning.ResumeProcessAsync(row.Pid));

        private void Action_Priority(object sender, RoutedEventArgs e) => ExecuteTuning(sender, async row =>
        {
            if (sender is FrameworkElement { Tag: string p })
            {
                uint priorityClass = p switch
                {
                    "Realtime" => 0x00000100,
                    "High" => 0x00000080,
                    "AboveNormal" => 0x00008000,
                    "Normal" => 0x00000020,
                    "BelowNormal" => 0x00004000,
                    "Idle" => 0x00000040,
                    _ => 0x00000020
                };
                await App.Current.ProcessTuning.SetPriorityAsync(row.Pid, priorityClass);
            }
        });

        private void Action_Eco(object sender, RoutedEventArgs e) => ExecuteTuning(sender, async row =>
        {
            if (sender is FrameworkElement { Tag: string eco })
            {
                await App.Current.ProcessTuning.SetEfficiencyAsync(row.Pid, eco == "Enabled");
            }
        });

        private void Action_Affinity(object sender, RoutedEventArgs e)
        {
            if (GetContextProcessRow(sender) is not { } row) return;
            _ = ShowProcessAffinityAsync(row);
        }

        private async Task ShowProcessAffinityAsync(TunerProcessRow row)
        {
            int cpus = App.Current.ProcessTuning.LogicalProcessorCount();
            ulong currentMask = await App.Current.ProcessTuning.GetAffinityAsync(row.Pid);

            var panel = new StackPanel { Spacing = 8 };
            var checkboxes = new List<CheckBox>();

            for (int i = 0; i < cpus; i++)
            {
                bool isSet = (currentMask & (1UL << i)) != 0;
                var cb = new CheckBox 
                { 
                    Content = $"CPU {i}", 
                    IsChecked = isSet, 
                    Tag = i 
                };
                checkboxes.Add(cb);
                panel.Children.Add(cb);
            }

            var dialog = new ContentDialog
            {
                Title = $"Processor Affinity: {row.Name} (current: 0x{currentMask:X})",
                Content = new ScrollViewer { Content = panel, MaxHeight = 400 },
                PrimaryButtonText = "Apply",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                ulong newMask = 0;
                foreach (var cb in checkboxes)
                {
                    if (cb.IsChecked == true && cb.Tag is int bit)
                    {
                        newMask |= (1UL << bit);
                    }
                }
                if (newMask != 0) 
                    await App.Current.ProcessTuning.SetAffinityAsync(row.Pid, newMask);
            }
        }

        private void ShowNotImplemented(string title)
        {
            _ = new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = "This module will be mapped to the kernel in a future update." },
                CloseButtonText = "Close",
                XamlRoot = this.XamlRoot
            }.ShowAsync();
        }

        private void Action_Boost(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string tag)
            {
                bool disable = tag == "Disabled";
                ExecuteTuning(sender, row => App.Current.ProcessTuning.SetPriorityBoostAsync(row.Pid, disable));
            }
        }


        private void Action_Threads(object sender, RoutedEventArgs e)
        {
            ExecuteTuning(sender, row =>
            {
                var wnd = new stellarisKIT.Controls.ThreadDiagnosticsWindow(row.Pid, row.Name);
                wnd.Activate();
                return Task.CompletedTask;
            });
        }
        
        private void Row_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            Action_Threads(sender, null);
        }

        private void Action_CreateRule(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is TunerProcessRow row)
            {
                var draft = new TunerProfile
                {
                    Name = row.Name,
                    Pattern = row.Name,
                    AutoApply = true,
                    Enabled = true,
                };
                _ = OpenRuleEditorAsync(draft, isNew: true);
            }
        }

        private void Action_Details(object sender, RoutedEventArgs e)
        {
            ExecuteTuning(sender, async row =>
            {
                using var proc = System.Diagnostics.Process.GetProcessById(row.Pid);
                var panel = new StackPanel { Spacing = 12 };
                
                string path = "Access Denied";
                try { path = proc.MainModule?.FileName ?? "Unknown"; } catch { }

                panel.Children.Add(new TextBlock { Text = $"Executable Path:\n{path}", TextWrapping = TextWrapping.Wrap, Style = (Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Application.Current.Resources["BodyTextBlockStyle"] });
                panel.Children.Add(new TextBlock { Text = $"Handles: {proc.HandleCount}", Style = (Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Application.Current.Resources["BodyTextBlockStyle"] });
                panel.Children.Add(new TextBlock { Text = $"Working Set (RAM): {(proc.WorkingSet64 / 1024 / 1024.0):F1} MB", Style = (Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Application.Current.Resources["BodyTextBlockStyle"] });
                try { panel.Children.Add(new TextBlock { Text = $"Start Time: {proc.StartTime}", Style = (Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Application.Current.Resources["BodyTextBlockStyle"] }); } catch { }

                var dialog = new ContentDialog
                {
                    Title = $"Details: {row.Name} ({row.Pid})",
                    Content = panel,
                    CloseButtonText = "Close",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            });
        }

        private void RefreshRulesMeta()
        {
            int n = ViewModel.Profiles.Count;
            RulesCountText.Text = n == 1 ? "1 rule" : $"{n} rules";
        }

        private TunerProfile? GetRuleRow(object sender) =>
            (sender as FrameworkElement)?.DataContext as TunerProfile;

        private TunerProfile? SelectedRuleOrRow(object sender) =>
            ViewModel.SelectedProfile ?? GetRuleRow(sender);

        private async Task OpenRuleEditorAsync(TunerProfile target, bool isNew)
        {
            var dlg = new RuleEditorDialog { XamlRoot = this.XamlRoot };
            dlg.LoadFrom(target, isNew);
            if (await dlg.ShowAsync() == ContentDialogResult.Primary || dlg.ShortcutAccepted)
            {
                target.CopyFrom(dlg.Draft);
                bool saved = await App.Current.ProfileWatcher.AddOrUpdate(target);
                ViewModel.SyncProfiles();
                if (!saved)
                {
                    await ShowRulesErrorAsync("Could not save the rule file. The rule is active for this session only.");
                }
                await App.Current.ProfileWatcher.ApplyProfileNowAsync(target);
                ViewModel.SyncProfiles();
                RefreshRulesMeta();
            }
        }

        private async void RuleAdd_Click(object sender, RoutedEventArgs e) =>
            await OpenRuleEditorAsync(new TunerProfile(), isNew: true);

        private async void RulePresets_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new PresetPickerDialog { XamlRoot = this.XamlRoot };
            ContentDialogResult choice;
            try
            {
                choice = await dlg.ShowAsync();
            }
            catch (Exception ex)
            {
                await ShowRulesErrorAsync("Could not open the presets dialog: " + ex.Message);
                return;
            }
            if (choice != ContentDialogResult.Primary) return;

            var selected = dlg.SelectedPresets;
            if (selected.Count == 0)
            {
                await ShowRulesErrorAsync("Select at least one preset (all rows are pre-selected).");
                return;
            }

            // Resolve affinity scopes against THIS machine's live topology.
            // Static masks can't ship in presets: core counts differ per box
            // and CPU Set IDs shift between boots.
            Func<AffinityScope, ulong?> resolve = scope =>
                !dlg.IncludeAffinity ? null : GamePresets.ComputeAffinityMask(_topologyCache, scope);
            try
            {
                _topologyCache = await App.Current.CpuSets.GetTopologyAsync();
            }
            catch
            {
                _topologyCache = new List<stellarisKIT.Native.CpuSetEntry>();
            }

            var fresh = GamePresets.BuildNew(selected, ViewModel.Profiles.Select(p => p.Pattern), resolve);
            if (fresh.Count == 0)
            {
                await ShowRulesErrorAsync("All selected presets are already present — nothing to add.");
                return;
            }

            int added = 0;
            bool saveOk = true;
            foreach (var profile in fresh)
            {
                saveOk &= await App.Current.ProfileWatcher.AddOrUpdate(profile);
                added++;
            }
            ViewModel.SyncProfiles();
            foreach (var profile in fresh)
            {
                await App.Current.ProfileWatcher.ApplyProfileNowAsync(profile);
            }
            ViewModel.SyncProfiles();
            RefreshRulesMeta();

            int skipped = selected.Sum(s => s.Profiles.Count) - fresh.Count;
            string affinityNote = !dlg.IncludeAffinity
                ? "Affinity skipped (unchecked)."
                : fresh.Any(p => p.AffinityMask.HasValue)
                    ? "Affinity was tuned to this PC's cores."
                    : "Affinity left untouched (no isolatable core split on this CPU).";
            await new ContentDialog
            {
                Title = saveOk ? "Presets added" : "Presets added (save failed)",
                Content = new TextBlock
                {
                    Text = $"Added {added} rule{(added == 1 ? "" : "s")}" +
                        (skipped > 0 ? $", skipped {skipped} already present" : "") + ".\n" + affinityNote +
                        (saveOk ? "" : "\nThe rule file could not be saved — rules are active for this session only."),
                    TextWrapping = TextWrapping.Wrap,
                },
                CloseButtonText = "Close",
                XamlRoot = this.XamlRoot,
            }.ShowAsync();
        }

        private Task ShowRulesErrorAsync(string message)
        {
            var dialog = new ContentDialog
            {
                Title = "Rules",
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "Close",
                XamlRoot = this.XamlRoot,
            };
            return dialog.ShowAsync().AsTask();
        }

        private async void RuleEdit_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedRuleOrRow(sender) is { } p)
            {
                await OpenRuleEditorAsync(p, isNew: false);
            }
        }

        private async void RuleToggle_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedRuleOrRow(sender) is not { } p) return;
            p.Enabled = !p.Enabled;
            p.RefreshSummaries();
            bool saved = await App.Current.ProfileWatcher.AddOrUpdate(p);
            ViewModel.SyncProfiles();
            if (!saved)
            {
                await ShowRulesErrorAsync("Could not save the rule file. The change is active for this session only.");
            }
            if (p.Enabled)
            {
                await App.Current.ProfileWatcher.ApplyProfileNowAsync(p);
            }
            ViewModel.SyncProfiles();
            RefreshRulesMeta();
        }

        private async void RuleDelete_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedRuleOrRow(sender) is not { } p) return;
            bool saved = await App.Current.ProfileWatcher.Remove(p);
            ViewModel.SyncProfiles();
            RefreshRulesMeta();
            if (!saved)
            {
                await ShowRulesErrorAsync("Could not save the rule file. The rule will reappear on restart.");
            }
        }

        private async void RuleApplyNow_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedRuleOrRow(sender) is not { } p) return;
            await App.Current.ProfileWatcher.ApplyProfileNowAsync(p);
            ViewModel.SyncProfiles();
            RefreshRulesMeta();
        }

        private void RuleRow_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            if (e.Handled) return;
            e.Handled = true;
            if (GetRuleRow(sender) is { } p)
            {
                _ = OpenRuleEditorAsync(p, isNew: false);
            }
        }
    }
}
