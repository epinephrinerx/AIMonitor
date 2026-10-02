using System.Windows;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Presentation.Wpf.Theme;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

[Collection("Wpf")]
public sealed class MainWindowTests
{
    [Fact]
    public void ConnectionsPage_Visibility_TogglesWithIsConnectionsPageVisible()
    {
        WpfTestHost.Run(() =>
        {
            var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
            var store = new BlockingSettingsStore(new AppSettings { ShowConnectionsAtStartup = false });
            using var session = new SettingsSession(store, new AppSettings { ShowConnectionsAtStartup = false });
            var vm = new MainWindowViewModel(coordinator, session);

            var window = new MainWindow(vm, session)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false,
                ShowActivated = false
            };
            try
            {
                window.Show();
                WpfTestHost.Realize(window);

                var wrapper = (UIElement)(window.FindName("ConnectionsWrapper") ?? window.FindName("ConnectionsHost")!);
                Assert.NotNull(wrapper);

                var tabBar = (UIElement)window.FindName("ProviderTabBar");
                Assert.NotNull(tabBar);

                var content = (UIElement)window.FindName("ProviderContentScrollViewer");
                Assert.NotNull(content);

                // Initial state: vm.IsConnectionsPageVisible = false
                Assert.False(vm.IsConnectionsPageVisible);
                Assert.Equal(Visibility.Collapsed, wrapper.Visibility);
                Assert.Equal(Visibility.Visible, tabBar.Visibility);
                Assert.Equal(Visibility.Visible, content.Visibility);

                // When IsConnectionsPageVisible is true: wrapper is Visible, tab bar & content Collapsed
                vm.IsConnectionsPageVisible = true;
                WpfTestHost.Realize(window);

                Assert.True(vm.IsConnectionsPageVisible);
                Assert.Equal(Visibility.Visible, wrapper.Visibility);
                Assert.Equal(Visibility.Collapsed, tabBar.Visibility);
                Assert.Equal(Visibility.Collapsed, content.Visibility);

                // Toggle back to false: wrapper is Collapsed, tab bar & content Visible
                vm.IsConnectionsPageVisible = false;
                WpfTestHost.Realize(window);

                Assert.False(vm.IsConnectionsPageVisible);
                Assert.Equal(Visibility.Collapsed, wrapper.Visibility);
                Assert.Equal(Visibility.Visible, tabBar.Visibility);
                Assert.Equal(Visibility.Visible, content.Visibility);
            }
            finally
            {
                window.Close();
                window.RequestExplicitExit();
                _ = coordinator.DisposeAsync();
            }
        });
    }
}
