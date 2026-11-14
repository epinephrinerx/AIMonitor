namespace AIMonitor.Presentation.Wpf.Tray;

/// <summary>A tray that can raise a balloon notification (1.3.3 <c>TrayController.notify</c>).</summary>
public interface ITrayNotifier
{
    void ShowBalloon(string title, string message);
}
