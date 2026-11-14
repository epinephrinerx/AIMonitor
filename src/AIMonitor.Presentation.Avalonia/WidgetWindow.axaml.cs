using System.ComponentModel;
using AIMonitor.Application.Settings;
using AIMonitor.Application.Windows;
using AIMonitor.Presentation.Avalonia.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace AIMonitor.Presentation.Avalonia;

/// <summary>
/// The widget window. It owns the timers rather than the view model, so the view model's rules stay
/// testable without a dispatcher: the four-second rotation, a one-second tick for "updated 5m ago",
/// and a short pause before a drag or a resize is remembered.
/// </summary>
public sealed partial class WidgetWindow : Window
{
    private readonly DispatcherTimer _rotation = new() { Interval = WidgetViewModel.RotationInterval };
    private readonly DispatcherTimer _statusTick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _saveSoon = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private WidgetViewModel? _model;
    private bool _placed;

    public WidgetWindow()
    {
        AvaloniaXamlLoader.Load(this);

        _rotation.Tick += (_, _) => _model?.Advance();
        _statusTick.Tick += (_, _) => _model?.RefreshStatus();
        _saveSoon.Tick += (_, _) =>
        {
            _saveSoon.Stop();
            SavePlacement();
        };

        DataContextChanged += (_, _) => Attach(DataContext as WidgetViewModel);
        Opened += (_, _) =>
        {
            RestorePlacement();
            ApplyRotationState();
            _statusTick.Start();
            PushLayout();
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                if (IsVisible)
                {
                    ApplyRotationState();
                    _statusTick.Start();
                }
                else
                {
                    _rotation.Stop();
                    _statusTick.Stop();
                }
            }
            else if (e.Property == BoundsProperty || e.Property == ClientSizeProperty)
            {
                PushLayout();
                if (_placed && IsVisible) _saveSoon.Stop();
                if (_placed && IsVisible) _saveSoon.Start();
            }
        };
        PositionChanged += (_, _) =>
        {
            if (!_placed || !IsVisible) return;
            _saveSoon.Stop();
            _saveSoon.Start();
        };
        Closing += (_, _) => SavePlacement();
        Closed += (_, _) =>
        {
            _rotation.Stop();
            _statusTick.Stop();
            _saveSoon.Stop();
        };

        var root = this.FindControl<Border>("Root")!;
        root.ContextRequested += (_, e) =>
        {
            ShowOptionsMenu(root);
            e.Handled = true;
        };
    }

    /// <summary>Set by the app; the widget's position and its menu choices are remembered through it.</summary>
    public SettingsSession? Session { get; set; }

    private void Attach(WidgetViewModel? model)
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnViewModelPropertyChanged;
            _model.RotationRestarted -= RestartRotation;
        }

        _model = model;
        if (_model is null)
        {
            _rotation.Stop();
            return;
        }

        _model.PropertyChanged += OnViewModelPropertyChanged;
        _model.RotationRestarted += RestartRotation;
        ApplyRotationState();
        PushLayout();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WidgetViewModel.IsRotating) or null) ApplyRotationState();
    }

    private void ApplyRotationState()
    {
        if (_model?.IsRotating == true && IsVisible)
        {
            if (!_rotation.IsEnabled) _rotation.Start();
        }
        else
        {
            _rotation.Stop();
        }
    }

    /// <summary>A chevron click gives the newly shown service a full interval.</summary>
    private void RestartRotation()
    {
        if (!_rotation.IsEnabled) return;
        _rotation.Stop();
        _rotation.Start();
    }

    /// <summary>Tells the view model how much room there is, which decides how many gauges fit.</summary>
    private void PushLayout()
    {
        if (_model is null) return;
        var size = ClientSize;
        if (size.Width <= 0 || size.Height <= 0) return;

        var header = this.FindControl<TextBlock>("HeaderText");
        var headerHeight = header?.Bounds.Height > 0 ? header.Bounds.Height : 16;
        // The captions are 10pt; a line of them is about 1.35 times that.
        const double lineHeight = 13.5;
        _model.UpdateLayout(size.Width, size.Height, headerHeight, lineHeight);
    }

    private static double MinimumUsefulHeight() => WidgetLayout.MinimumUsefulHeight(16, 13.5);

    /// <summary>Back where it was on this arrangement of displays; the default size otherwise (PAR-021).</summary>
    private void RestorePlacement()
    {
        if (_placed) return;
        _placed = true;
        if (Session is null) return;

        var minimum = MinimumUsefulHeight();
        if (!WindowGeometry.TryRestore(this, Session, WindowGeometryManager.WidgetMode,
                (width, height) => WidgetLayout.Clamp(width, height, minimum)))
        {
            var (width, height) = WidgetLayout.Clamp(WidgetLayout.DefaultWidth, WidgetLayout.DefaultHeight, minimum);
            Width = width;
            Height = height;
        }
    }

    public void SavePlacement()
    {
        if (Session is null || !IsVisible || !_placed) return;
        _ = WindowGeometry.SaveAsync(this, Session, WindowGeometryManager.WidgetMode);
    }

    /// <summary>
    /// The right-click menu of the Windows widget: open the dashboard, refresh, Show ▸ (all services
    /// taking turns, or one), Always on top, a live Opacity slider, Quit. Built on each opening so
    /// its check marks match what is on show.
    /// </summary>
    private void ShowOptionsMenu(Control anchor)
    {
        if (_model is not { } model) return;

        var menu = new ContextMenu();
        menu.Items.Add(Entry("Expand to dashboard", () => model.ExpandCommand.Execute(null)));
        menu.Items.Add(Entry("Refresh now", () => model.RefreshCommand.Execute(null)));
        menu.Items.Add(new Separator());

        if (model.Providers.Count > 1)
        {
            var show = new MenuItem { Header = "Show" };
            var all = new MenuItem { Header = "All services (rotate)", ToggleType = MenuItemToggleType.Radio, IsChecked = model.IsRotatingAll };
            all.Click += (_, _) =>
            {
                model.ShowAllProviders();
                Persist(session => WidgetPreferenceRecorder.SetRotationAsync(session, true));
            };
            show.Items.Add(all);
            show.Items.Add(new Separator());

            var showing = model.Current.ProviderId;
            foreach (var provider in model.Providers)
            {
                var id = provider.ProviderId;
                var entry = new MenuItem
                {
                    Header = provider.Title,
                    ToggleType = MenuItemToggleType.Radio,
                    IsChecked = !model.IsRotatingAll && id == showing,
                };
                entry.Click += (_, _) =>
                {
                    model.ShowProvider(id);
                    Persist(session => WidgetPreferenceRecorder.SetRotationAsync(session, false));
                };
                show.Items.Add(entry);
            }

            menu.Items.Add(show);
        }

        var onTop = new MenuItem { Header = "Always on top", ToggleType = MenuItemToggleType.CheckBox, IsChecked = model.AlwaysOnTop };
        onTop.Click += (_, _) =>
        {
            var enabled = !model.AlwaysOnTop;
            model.AlwaysOnTop = enabled;
            Persist(session => WidgetPreferenceRecorder.SetAlwaysOnTopAsync(session, enabled));
        };
        menu.Items.Add(onTop);
        menu.Items.Add(OpacityRow(model, menu));
        menu.Items.Add(new Separator());
        menu.Items.Add(Entry("Quit", () => model.QuitCommand.Execute(null)));

        menu.Open(anchor);
    }

    private static MenuItem Entry(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>A live slider rather than fixed steps: opacity is judged by eye, so it applies while dragging.</summary>
    private Control OpacityRow(WidgetViewModel model, ContextMenu menu)
    {
        var readout = new TextBlock
        {
            Text = $"{Math.Round(model.Opacity * 100)}%",
            Width = 40,
            TextAlignment = global::Avalonia.Media.TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var slider = new Slider
        {
            Minimum = SettingsViewModel.MinimumOpacity * 100,
            Maximum = 100,
            SmallChange = 5,
            LargeChange = 10,
            Value = Math.Round(model.Opacity * 100),
            Width = 140,
            Margin = new Thickness(8, 0),
        };
        slider.ValueChanged += (_, args) =>
        {
            readout.Text = $"{Math.Round(args.NewValue)}%";
            model.Opacity = Math.Round(args.NewValue) / 100.0;
        };
        // Saved once, when the menu closes, rather than on every step of the drag.
        menu.Closed += (_, _) =>
        {
            var value = model.Opacity;
            Persist(session => WidgetPreferenceRecorder.SetOpacityAsync(session, value));
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 4) };
        row.Children.Add(new TextBlock { Text = "Opacity", VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(slider);
        row.Children.Add(readout);
        return row;
    }

    private void Persist(Func<SettingsSession, Task> write)
    {
        if (Session is null) return;
        _ = PersistAsync(write(Session));
    }

    private static async Task PersistAsync(Task write)
    {
        try
        {
            await write.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // A menu choice that could not be saved still applies for this run.
        }
    }

    /// <summary>A frameless window has no title bar, so dragging the card itself moves it.</summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e) => _model?.ExpandCommand.Execute(null);
}
