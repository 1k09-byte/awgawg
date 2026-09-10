using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using stellarisKIT.Services;
using stellarisKIT.Native;

namespace stellarisKIT
{
    public partial class App : Application
    {
        public static new App Current => (App)Application.Current;

        private Window? _window;
        private static Window? _windowStatic;
        private ThemeService _themeService;
        private InstallerService _installerService;

        public ThemeService ThemeService { get { return _themeService; } }
        public InstallerService InstallerService { get { return _installerService; } }

        /// <summary>
        /// The app's main window. Needed by file pickers and dialogs in pages
        /// that don't hold a window reference (WinUI 3 unpackaged pattern).
        /// </summary>
        public static Window? MainWindow => _windowStatic;
        
        public ProcessTuningService ProcessTuning { get; } = new ProcessTuningService();
        public ThreadTuningService ThreadTuning { get; } = new ThreadTuningService();
        public CpuSetService CpuSets { get; } = new CpuSetService();
        public NativeSnapshotService NativeSnapshot { get; } = new NativeSnapshotService();

        /// <summary>
        /// Shared Gaming mode instance: the main process list and the Threads
        /// window both go through this, so there is exactly one restore map
        /// and the two UIs can never fight over priorities.
        /// </summary>
        public GamingModeService GamingMode { get; } = new GamingModeService();
        public ProfileWatcherService ProfileWatcher { get; }

        public App()
        {
            InitializeComponent();
            _themeService = new ThemeService();
            _installerService = new InstallerService();
            
            ProfileWatcher = new ProfileWatcherService(ProcessTuning, CpuSets, ThreadTuning);
            // Fire and forget the profile watcher async load
            _ = InitializeWatcherAsync();
        }

        private async Task InitializeWatcherAsync()
        {
            await ProfileWatcher.LoadProfilesAsync();
            ProfileWatcher.StartWatcher();
            // Arm Gaming mode for gaming-mode rules whose process is already
            // running (app started mid-game) and watch for exits.
            ProfileWatcher.StartGamingModeWatcher();
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            _windowStatic = _window;
            ThemeService.Initialize(_window);
            _window.Activate();
        }
    }
}
