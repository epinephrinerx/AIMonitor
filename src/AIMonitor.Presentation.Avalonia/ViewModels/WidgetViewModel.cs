using System.Collections.ObjectModel;
using System.Windows.Input;
using AIMonitor.Application.Settings;

namespace AIMonitor.Presentation.Avalonia.ViewModels;

/// <summary>
/// The always-on-top widget: one service at a time, small enough to leave in a corner of the screen.
///
/// Holds no timer of its own — the window drives the rotation by calling <see cref="Advance"/>, so
/// every rule here can be tested without a dispatcher.
/// </summary>
public sealed class WidgetViewModel : ObservableObject
{
    /// <summary>More than this and the widget stops being glanceable.</summary>
    private const int MaxVisibleMeters = 3;

    private readonly IReadOnlyList<ProviderViewModel> _providers;
    private int _position;
    private string? _pinnedProviderId;
    private bool _rotationEnabled = true;
    private double _opacity = 1;
    private bool _alwaysOnTop = true;

    public WidgetViewModel(IReadOnlyList<ProviderViewModel> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        if (providers.Count == 0)
        {
            throw new ArgumentException("The widget needs at least one service to show.", nameof(providers));
        }

        _providers = providers;
        VisibleMeters = [];

        NextCommand = new RelayCommand(() => Step(1));
        PreviousCommand = new RelayCommand(() => Step(-1));
        ExpandCommand = new RelayCommand(() => ExpandRequested?.Invoke());
        RefreshCommand = new RelayCommand(() => RefreshRequested?.Invoke());
        QuitCommand = new RelayCommand(() => QuitRequested?.Invoke());

        foreach (var provider in _providers)
        {
            provider.PropertyChanged += (_, _) => RefreshVisibleMeters();
        }

        RefreshVisibleMeters();
    }

    public event Action? ExpandRequested;

    public event Action? RefreshRequested;

    public event Action? QuitRequested;

    public ICommand NextCommand { get; }

    public ICommand PreviousCommand { get; }

    public ICommand ExpandCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand QuitCommand { get; }

    public ObservableCollection<MeterViewModel> VisibleMeters { get; }

    public ProviderViewModel Current => _providers[Math.Clamp(_position, 0, _providers.Count - 1)];

    public string Title => Current.Title;

    public bool CanPage => _providers.Count > 1;

    /// <summary>Pinning a service stops the rotation on it until the user pages away again.</summary>
    public string? PinnedProviderId => _pinnedProviderId;

    public bool IsPinned
    {
        get => _pinnedProviderId is not null;
        set
        {
            var pinned = value ? Current.ProviderId : null;
            if (_pinnedProviderId == pinned) return;
            _pinnedProviderId = pinned;
            Raise();
            Raise(nameof(IsRotating));
        }
    }

    public bool RotationEnabled
    {
        get => _rotationEnabled;
        private set
        {
            if (Set(ref _rotationEnabled, value)) Raise(nameof(IsRotating));
        }
    }

    /// <summary>Whether the widget should actually be cycling right now.</summary>
    public bool IsRotating => _rotationEnabled && _pinnedProviderId is null && CanPage;

    public double Opacity
    {
        get => _opacity;
        private set => Set(ref _opacity, value);
    }

    public bool AlwaysOnTop
    {
        get => _alwaysOnTop;
        private set => Set(ref _alwaysOnTop, value);
    }

    public bool HasMeters => VisibleMeters.Count > 0;

    public string EmptyMessage => Current.HasError ? "Last refresh failed" : "No quota data yet";

    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Opacity = Math.Clamp(settings.WidgetOpacity, 0.2, 1.0);
        AlwaysOnTop = settings.WidgetAlwaysOnTop;
        RotationEnabled = settings.WidgetRotationEnabled;
    }

    /// <summary>One step of the rotation. Does nothing while pinned or with one service.</summary>
    public void Advance()
    {
        if (!IsRotating) return;
        Step(1);
    }

    /// <summary>Shows this service, if it is one of ours. Used when the dashboard's tab changes.</summary>
    public void Show(string? providerId)
    {
        if (providerId is null) return;
        var index = IndexOf(providerId);
        if (index < 0 || index == _position) return;

        _position = index;
        OnPositionChanged();
    }

    private int IndexOf(string providerId)
    {
        for (var i = 0; i < _providers.Count; i++)
        {
            if (string.Equals(_providers[i].ProviderId, providerId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private void Step(int delta)
    {
        if (!CanPage) return;
        _position = ((_position + delta) % _providers.Count + _providers.Count) % _providers.Count;
        OnPositionChanged();
    }

    private void OnPositionChanged()
    {
        // Paging by hand pins the service it lands on: the user asked to look at this one, and
        // having it slide away two seconds later would undo the request.
        if (_pinnedProviderId is not null)
        {
            _pinnedProviderId = Current.ProviderId;
        }

        Raise(nameof(Current));
        Raise(nameof(Title));
        Raise(nameof(EmptyMessage));
        RefreshVisibleMeters();
    }

    private void RefreshVisibleMeters()
    {
        var wanted = Current.Meters
            .OrderByDescending(meter => meter.IsFiveHour)
            .ThenByDescending(meter => meter.Percent ?? -1)
            .Take(MaxVisibleMeters)
            .ToList();

        VisibleMeters.Clear();
        foreach (var meter in wanted)
        {
            VisibleMeters.Add(meter);
        }

        Raise(nameof(HasMeters));
        Raise(nameof(EmptyMessage));
    }
}
