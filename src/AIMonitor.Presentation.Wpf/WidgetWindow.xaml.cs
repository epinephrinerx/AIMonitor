using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AIMonitor.Application.Settings;
using AIMonitor.Application.Windows;
using AIMonitor.Presentation.Wpf.ViewModels;
using Orientation = System.Windows.Controls.Orientation;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace AIMonitor.Presentation.Wpf;

public partial class WidgetWindow : Window
{
    private readonly WidgetViewModel? _viewModel;
    private readonly SettingsSession? _session;
    private readonly DispatcherTimer _resizeSaveTimer;
    private readonly DispatcherTimer _statusTimer;
    private Task _lastSaveTask = Task.CompletedTask;

    public WidgetWindow()
    {
        InitializeComponent();

        _resizeSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _resizeSaveTimer.Tick += OnResizeSaveTimerTick;

        // The "updated … ago" footer ticks while the widget is on screen.
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => (_viewModel ?? DataContext as WidgetViewModel)?.RefreshStatus();

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        LocationChanged += OnLocationChanged;
        IsVisibleChanged += OnIsVisibleChanged;
        MouseRightButtonUp += OnWindowRightClick;
        Closing += OnClosing;

        DataContextChanged += (s, e) =>
        {
            if (e.OldValue is WidgetViewModel oldVm)
            {
                oldVm.PropertyChanged -= OnViewModelPropertyChanged;
            }
            if (e.NewValue is WidgetViewModel newVm)
            {
                newVm.PropertyChanged += OnViewModelPropertyChanged;
                UpdateRowHeights();
            }
        };
    }

    public WidgetWindow(WidgetViewModel viewModel, SettingsSession session)
        : this()
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        DataContext = viewModel;

        RestorePlacement();
    }

    /// <summary>
    /// Restores saved window placement (position and size) clamped through <see cref="WidgetLayout.Clamp"/>
    /// based on font metrics. Defaults to 230x175 if no saved placement exists.
    /// </summary>
    public void RestorePlacement()
    {
        var (h, l) = MeasureFontMetrics(this);
        var minUsefulH = WidgetLayout.MinimumUsefulHeight(h, l);
        var (_, minH) = WidgetLayout.Clamp(WidgetLayout.MinWidth, 0, minUsefulH);
        MinHeight = minH;

        if (_session is not null)
        {
            var displays = GetDisplayAreas();
            var topologyId = WindowGeometryManager.GenerateTopologyId(displays);

            if (WindowGeometryManager.TryRestorePlacement(_session.Current, topologyId, WindowGeometryManager.WidgetMode, displays, out var placement))
            {
                Left = placement.Left;
                Top = placement.Top;

                var (clampedW, clampedH) = WidgetLayout.Clamp(placement.Width, placement.Height, minUsefulH);
                Width = clampedW;
                Height = clampedH;

                var vm = _viewModel ?? DataContext as WidgetViewModel;
                vm?.UpdateLayout(clampedW, clampedH, h, l);
                UpdateRowHeights();
                return;
            }
        }

        var (defaultW, defaultH) = WidgetLayout.Clamp(WidgetLayout.DefaultWidth, WidgetLayout.DefaultHeight, minUsefulH);
        Width = defaultW;
        Height = defaultH;

        var defaultVm = _viewModel ?? DataContext as WidgetViewModel;
        defaultVm?.UpdateLayout(defaultW, defaultH, h, l);
        UpdateRowHeights();
    }

    /// <summary>
    /// Measures font metrics for the header (13pt bold) and caption (~10pt) used in the widget layout.
    /// </summary>
    internal static (double HeaderHeight, double LineHeight) MeasureFontMetrics(
        Visual? visual = null,
        System.Windows.Media.FontFamily? fontFamily = null,
        double headerFontSize = 13.0,
        double captionFontSize = 10.0,
        double fallbackPixelsPerDip = 1.0)
    {
        var family = fontFamily ?? System.Windows.SystemFonts.MessageFontFamily;
        double ppd = fallbackPixelsPerDip;
        if (visual is not null)
        {
            try
            {
                ppd = VisualTreeHelper.GetDpi(visual).PixelsPerDip;
            }
            catch
            {
                // Fall back when visual is not yet attached to a presentation source
            }
        }

        var headerTypeface = new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var headerFormatted = new FormattedText(
            "Mg",
            CultureInfo.InvariantCulture,
            System.Windows.FlowDirection.LeftToRight,
            headerTypeface,
            headerFontSize,
            System.Windows.Media.Brushes.Black,
            ppd);

        var captionTypeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var captionFormatted = new FormattedText(
            "Mg",
            CultureInfo.InvariantCulture,
            System.Windows.FlowDirection.LeftToRight,
            captionTypeface,
            captionFontSize,
            System.Windows.Media.Brushes.Black,
            ppd);

        return (headerFormatted.Height, captionFormatted.Height);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var (h, l) = MeasureFontMetrics(this);
        var minUsefulH = WidgetLayout.MinimumUsefulHeight(h, l);
        var (_, minH) = WidgetLayout.Clamp(WidgetLayout.MinWidth, 0, minUsefulH);
        MinHeight = minH;

        double currentW = ActualWidth > 0 ? ActualWidth : Width;
        double currentH = ActualHeight > 0 ? ActualHeight : Height;
        var (clampedW, clampedH) = WidgetLayout.Clamp(currentW, currentH, minUsefulH);
        Width = clampedW;
        Height = clampedH;

        var vm = _viewModel ?? DataContext as WidgetViewModel;
        vm?.UpdateLayout(clampedW, clampedH, h, l);
        UpdateRowHeights();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var (h, l) = MeasureFontMetrics(this);

        var vm = _viewModel ?? DataContext as WidgetViewModel;
        double w = e.NewSize.Width > 0 ? e.NewSize.Width : (ActualWidth > 0 ? ActualWidth : Width);
        double height = e.NewSize.Height > 0 ? e.NewSize.Height : (ActualHeight > 0 ? ActualHeight : Height);
        vm?.UpdateLayout(w, height, h, l);
        UpdateRowHeights();

        if (_session is not null && IsLoaded)
        {
            _resizeSaveTimer.Stop();
            _resizeSaveTimer.Start();
        }
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (_session is not null && IsLoaded)
        {
            _resizeSaveTimer.Stop();
            _resizeSaveTimer.Start();
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var vm = _viewModel ?? DataContext as WidgetViewModel;
        var visible = IsVisible;
        vm?.SetActive(visible);
        if (visible)
        {
            vm?.RefreshStatus();
            _statusTimer.Start();
            return;
        }

        // Leaving widget mode (expand, tray) keeps the position the user dragged it to.
        _statusTimer.Stop();
        if (IsLoaded)
        {
            _resizeSaveTimer.Stop();
            SaveGeometry();
        }
    }

    private void OnWindowRightClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsPointInResizeBand(e.GetPosition(this)))
        {
            ShowOptionsMenu();
            e.Handled = true;
        }
    }

    /// <summary>The right-click menu of 1.3.3: Expand, Refresh, Show, Always on top, Opacity, Quit.</summary>
    private void ShowOptionsMenu()
    {
        var vm = _viewModel ?? DataContext as WidgetViewModel;
        if (vm is null)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        menu.Items.Add(MenuEntry("Expand to dashboard", vm.Expand));
        menu.Items.Add(MenuEntry("Refresh now", vm.Refresh));
        menu.Items.Add(new Separator());

        if (vm.Providers.Count > 1)
        {
            var show = new MenuItem { Header = "Show" };
            var all = new MenuItem { Header = "All services (rotate)", IsCheckable = true, IsChecked = vm.IsRotatingAll };
            all.Click += (_, _) =>
            {
                vm.ShowAllProviders();
                Persist(session => WidgetPreferenceRecorder.SetRotationAsync(session, true));
            };
            show.Items.Add(all);
            show.Items.Add(new Separator());

            var showing = vm.CurrentProvider?.ProviderId;
            foreach (var provider in vm.Providers)
            {
                var id = provider.ProviderId;
                var entry = new MenuItem
                {
                    Header = provider.DisplayName,
                    IsCheckable = true,
                    IsChecked = !vm.IsRotatingAll && id == showing,
                };
                entry.Click += (_, _) =>
                {
                    vm.ShowProvider(id);
                    Persist(session => WidgetPreferenceRecorder.SetRotationAsync(session, false));
                };
                show.Items.Add(entry);
            }

            menu.Items.Add(show);
        }

        var onTop = new MenuItem { Header = "Always on top", IsCheckable = true, IsChecked = vm.AlwaysOnTop };
        onTop.Click += (_, _) =>
        {
            var enabled = onTop.IsChecked;
            vm.AlwaysOnTop = enabled;
            Persist(session => WidgetPreferenceRecorder.SetAlwaysOnTopAsync(session, enabled));
        };
        menu.Items.Add(onTop);
        menu.Items.Add(BuildOpacityRow(vm, menu));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Quit", vm.Quit));
        menu.IsOpen = true;
    }

    private static MenuItem MenuEntry(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>A live slider rather than fixed steps: opacity is judged by eye, so it applies while dragging.</summary>
    private FrameworkElement BuildOpacityRow(WidgetViewModel vm, ContextMenu menu)
    {
        var caption = new TextBlock { Text = "Opacity", VerticalAlignment = VerticalAlignment.Center };
        var readout = new TextBlock
        {
            Text = $"{Math.Round(vm.Opacity * 100)}%",
            Width = 36,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
        };
        var slider = new Slider
        {
            Minimum = 25,
            Maximum = 100,
            SmallChange = 5,
            LargeChange = 10,
            Value = Math.Round(vm.Opacity * 100),
            Width = 140,
            Margin = new Thickness(8, 0, 8, 0),
        };
        slider.ValueChanged += (_, args) =>
        {
            readout.Text = $"{Math.Round(args.NewValue)}%";
            vm.Opacity = Math.Round(args.NewValue) / 100.0;
        };
        // Persist once, when the menu closes, rather than on every tick of the drag.
        menu.Closed += (_, _) =>
        {
            var value = vm.Opacity;
            Persist(session => WidgetPreferenceRecorder.SetOpacityAsync(session, value));
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 4, 12, 4) };
        row.Children.Add(caption);
        row.Children.Add(slider);
        row.Children.Add(readout);
        return row;
    }

    private void Persist(Func<SettingsSession, Task> write)
    {
        if (_session is null)
        {
            return;
        }

        _ = FireAndForgetAsync(write(_session));
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WidgetViewModel.HeaderRowHeight) or nameof(WidgetViewModel.FooterRowHeight))
        {
            UpdateRowHeights();
        }
    }

    private void UpdateRowHeights()
    {
        var vm = _viewModel ?? DataContext as WidgetViewModel;
        if (vm is not null && HeaderRowDefinition is not null && FooterRowDefinition is not null)
        {
            HeaderRowDefinition.Height = new GridLength(vm.HeaderRowHeight);
            FooterRowDefinition.Height = new GridLength(vm.FooterRowHeight);
        }
    }

    internal bool IsResizeSavePending => _resizeSaveTimer.IsEnabled;

    internal void OnResizeSaveTimerTick(object? sender, EventArgs e)
    {
        _resizeSaveTimer.Stop();
        SaveGeometry();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (DataContext is WidgetViewModel vm)
        {
            vm.PropertyChanged -= OnViewModelPropertyChanged;
        }
        _resizeSaveTimer.Stop();
        SaveGeometry();
    }

    public Task SaveGeometryAsync()
    {
        if (_session is null) return Task.CompletedTask;

        try
        {
            var displays = GetDisplayAreas();
            double width = ActualWidth > 0 ? ActualWidth : Width;
            double height = ActualHeight > 0 ? ActualHeight : Height;
            var placement = new WindowPlacement(Left, Top, width, height, false);
            var task = WindowPlacementRecorder.RecordAsync(_session, WindowGeometryManager.WidgetMode, placement, displays, DateTimeOffset.UtcNow);
            _lastSaveTask = task;
            return task;
        }
        catch
        {
            return Task.CompletedTask;
        }
    }

    public void SaveGeometry()
    {
        // Fire-and-forget helper observes and catches any task exception to avoid unobserved task exceptions on exit.
        _ = FireAndForgetAsync(SaveGeometryAsync());
    }

    internal Task WaitForPendingSaveAsync() => _lastSaveTask;

    private static async Task FireAndForgetAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Non-fatal: ignored to prevent unobserved task exceptions
        }
    }

    private static IReadOnlyList<DisplayArea> GetDisplayAreas()
    {
        return System.Windows.Forms.Screen.AllScreens
            .Select(s => new DisplayArea(s.Bounds.Left, s.Bounds.Top, s.Bounds.Width, s.Bounds.Height))
            .ToList();
    }

    internal bool IsPointInResizeBand(System.Windows.Point pt)
    {
        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : Height;

        return pt.X < WidgetLayout.ResizeMargin ||
               pt.Y < WidgetLayout.ResizeMargin ||
               pt.X >= w - WidgetLayout.ResizeMargin ||
               pt.Y >= h - WidgetLayout.ResizeMargin;
    }

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        var pt = e.GetPosition(this);
        if (IsPointInResizeBand(pt))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            (System.Windows.Application.Current as App)?.SwitchToDashboardMode();
            return;
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }
}
