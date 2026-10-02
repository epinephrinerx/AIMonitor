using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AIMonitor.Application.Settings;
using AIMonitor.Application.Windows;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf;

public partial class WidgetWindow : Window
{
    private readonly WidgetViewModel? _viewModel;
    private readonly SettingsSession? _session;
    private readonly DispatcherTimer _resizeSaveTimer;
    private Task _lastSaveTask = Task.CompletedTask;

    public WidgetWindow()
    {
        InitializeComponent();

        _resizeSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _resizeSaveTimer.Tick += OnResizeSaveTimerTick;

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        Closing += OnClosing;
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
                return;
            }
        }

        var (defaultW, defaultH) = WidgetLayout.Clamp(WidgetLayout.DefaultWidth, WidgetLayout.DefaultHeight, minUsefulH);
        Width = defaultW;
        Height = defaultH;
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
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var (h, l) = MeasureFontMetrics(this);

        var vm = _viewModel ?? DataContext as WidgetViewModel;
        double w = e.NewSize.Width > 0 ? e.NewSize.Width : (ActualWidth > 0 ? ActualWidth : Width);
        double height = e.NewSize.Height > 0 ? e.NewSize.Height : (ActualHeight > 0 ? ActualHeight : Height);
        vm?.UpdateLayout(w, height, h, l);

        if (_session is not null && IsLoaded)
        {
            _resizeSaveTimer.Stop();
            _resizeSaveTimer.Start();
        }
    }

    private void OnResizeSaveTimerTick(object? sender, EventArgs e)
    {
        _resizeSaveTimer.Stop();
        SaveGeometry();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
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

    private void OnOpenDashboardClick(object sender, RoutedEventArgs e)
    {
        (System.Windows.Application.Current as App)?.SwitchToDashboardMode();
    }
}
