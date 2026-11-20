using AIMonitor.Presentation.Avalonia.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace AIMonitor.Presentation.Avalonia;

/// <summary>
/// The widget window. It owns the rotation timer rather than the view model, so the view model's
/// rules stay testable without a dispatcher.
/// </summary>
public sealed partial class WidgetWindow : Window
{
    /// <summary>The pace the Windows tray cycles services at; the same one reads well here.</summary>
    private static readonly TimeSpan RotateEvery = TimeSpan.FromSeconds(2);

    private readonly DispatcherTimer _rotation;

    public WidgetWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _rotation = new DispatcherTimer { Interval = RotateEvery };
        _rotation.Tick += (_, _) => (DataContext as WidgetViewModel)?.Advance();
        DataContextChanged += (_, _) => SyncRotation();
        Opened += (_, _) => SyncRotation();
        Closed += (_, _) => _rotation.Stop();
    }

    private WidgetViewModel? ViewModel => DataContext as WidgetViewModel;

    private void SyncRotation()
    {
        if (ViewModel is not { } model)
        {
            _rotation.Stop();
            return;
        }

        model.PropertyChanged -= OnViewModelPropertyChanged;
        model.PropertyChanged += OnViewModelPropertyChanged;
        ApplyRotationState();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(WidgetViewModel.IsRotating) or null)
        {
            ApplyRotationState();
        }
    }

    private void ApplyRotationState()
    {
        if (ViewModel?.IsRotating == true)
        {
            _rotation.Start();
        }
        else
        {
            _rotation.Stop();
        }
    }

    /// <summary>A frameless window has no title bar, so dragging the card itself moves it.</summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e) => ViewModel?.ExpandCommand.Execute(null);

    private void OnTogglePin(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } model) return;
        model.IsPinned = !model.IsPinned;
        if (sender is MenuItem item)
        {
            item.Header = model.IsPinned ? "Let services take turns again" : "Keep this service in view";
        }
    }
}
