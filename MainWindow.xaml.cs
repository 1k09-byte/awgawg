using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using stellarisKIT.Pages;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Foundation;
using WinRT.Interop;

namespace stellarisKIT
{
    public sealed partial class MainWindow : Window
    {
        // DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2.
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public MainWindow()
        {
            InitializeComponent();

            // Explicitly request Windows 11 rounded window corners (the green-check
            // shape in the reference) instead of inheriting whatever the default
            // resolves to with a custom title bar + Mica backdrop.
            try
            {
                IntPtr hwnd = WindowNative.GetWindowHandle(this);
                int preference = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            }
            catch
            {
                // Cosmetic only: a square window is still fully usable.
            }

            ExtendsContentIntoTitleBar = true;
            AppWindow.Resize(new Windows.Graphics.SizeInt32 { Width = 1440, Height = 900 });
            AppWindow.Title = "kit";
            AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;

            // Default to Installer page and select the nav item.
            // Pill position is layout-driven (LayoutUpdated): event-driven updates
            // (SelectionChanged/SizeChanged) can compute against a stale layout
            // pass when maximizing/restoring, stranding the pill on the wrong item.
            NavShell.LayoutUpdated += (_, _) => UpdateNavPill();
            NavView.SelectedItem = NavView.MenuItems[0];
            NavView.Loaded += (_, _) => SuppressSidebarTooltips();
            SuppressSidebarTooltips();
        }

        private void SuppressSidebarTooltips()
        {
            foreach (var item in System.Linq.Enumerable.OfType<NavigationViewItem>(NavView.MenuItems)
                .Concat(System.Linq.Enumerable.OfType<NavigationViewItem>(NavView.FooterMenuItems)))
            {
                ToolTipService.SetToolTip(item, new ToolTip { Visibility = Visibility.Collapsed });
            }
        }

        /// <summary>
        /// Moves the shared <c>NavPill</c> to the vertical center of the selected
        /// rail item. Margin changes are glide-animated by the pill's
        /// <c>RepositionThemeTransition</c>. Runs on every layout pass but only
        /// assigns when the target actually moved (epsilon guard), so the
        /// assignment itself can't cause a layout loop. No-ops before first layout.
        /// </summary>
        private void UpdateNavPill(NavigationViewItem? item = null)
        {
            item ??= NavView.SelectedItem as NavigationViewItem;
            if (item is null || item.ActualHeight <= 0 || NavShell.ActualHeight <= 0)
            {
                return;
            }

            var pos = item.TransformToVisual(NavShell).TransformPoint(new Point(0, 0));
            double top = pos.Y + ((item.ActualHeight - NavPill.Height) / 2);

            // Pill sits flush against the highlight box's start (left) edge line.
            var box = FindDescendant<Border>(item, "BackgroundPill");
            if (box is null || box.ActualWidth <= 0)
            {
                return;
            }

            var boxPos = box.TransformToVisual(NavShell).TransformPoint(new Point(0, 0));
            double left = boxPos.X;
            if (Math.Abs(NavPill.Margin.Top - top) > 0.5 || Math.Abs(NavPill.Margin.Left - left) > 0.5)
            {
                NavPill.Margin = new Thickness(left, top, 0, 0);
            }

            NavPill.Opacity = 1;
        }

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.IsSettingsSelected)
            {
                ContentFrame.Navigate(typeof(SettingsPage));
                return;
            }

            if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            {
                switch (tag)
                {
                    case "Home": // legacy fallback
                    case "AppsPage":
                         ContentFrame.Navigate(typeof(InstallerPage));
                         break;
                    case "DriversPage":
                         ContentFrame.Navigate(typeof(GpuDriversPage));
                         break;
                    case "GamingPage":
                         ContentFrame.Navigate(typeof(AffinityPage));
                         break;
                    case "ThreadTunerPage":
                         ContentFrame.Navigate(typeof(ThreadTunerPage));
                         break;
                    case "WindowsSettingsPage":
                         ContentFrame.Navigate(typeof(WindowsSettingsPage));
                         break;
                    case "Settings":
                         ContentFrame.Navigate(typeof(SettingsPage));
                         break;
                }

                UpdateNavPill(item);
                PlaySelectPop(item);
            }
        }

        /// <summary>
        /// Plays the selection effect on the rail icon. Driven from code (not a
        /// VSM storyboard) so hovering can neither cancel nor retrigger it.
        /// Crispness-first: transform scale/rotate re-rasterizes the glyph as a
        /// bitmap mid-flight (blur), so the scale pop is kept small and the
        /// effect is carried by an opacity flash (alpha-only, always sharp).
        /// No-ops if the template isn't applied yet.
        /// </summary>
        private static void PlaySelectPop(NavigationViewItem item)
        {
            var presenter = FindDescendant<ContentPresenter>(item, "IconPresenter");
            if (presenter?.RenderTransform is not TransformGroup group
                || group.Children.Count < 1
                || group.Children[0] is not ScaleTransform)
            {
                return;
            }

            var board = new Storyboard();
            var duration = TimeSpan.FromMilliseconds(300);

            DoubleAnimation Track(string property, double from, double to, double amplitude)
            {
                var anim = new DoubleAnimation
                {
                    From = from,
                    To = to,
                    Duration = duration,
                    EasingFunction = new BackEase { Amplitude = amplitude, EasingMode = EasingMode.EaseOut },
                };
                Storyboard.SetTarget(anim, group.Children[0]);
                Storyboard.SetTargetProperty(anim, property);
                board.Children.Add(anim);
                return anim;
            }

            Track("ScaleX", 0.8, 1, 1.4);
            Track("ScaleY", 0.8, 1, 1.4);

            // Opacity flash: alpha blending never re-rasterizes, stays razor sharp.
            // FillBehavior Stop releases back to the Selected state's breathing
            // loop once the flash finishes (otherwise this HoldEnd value would
            // smother it).
            var flash = new DoubleAnimation
            {
                From = 1,
                To = 0.45,
                Duration = TimeSpan.FromMilliseconds(150),
                AutoReverse = true,
                FillBehavior = FillBehavior.Stop,
            };
            Storyboard.SetTarget(flash, presenter);
            Storyboard.SetTargetProperty(flash, "Opacity");
            board.Children.Add(flash);

            board.Begin();
        }

        private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match && match.Name == name)
                {
                    return match;
                }

                var found = FindDescendant<T>(child, name);
                if (found is not null)
                {
                    return found;
                }
            }

            return null;
        }
    }
}
