using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace DiskLoom;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(1440, 900));
        AppWindow.SetPresenter(AppWindowPresenterKind.Default);
        WindowRoot.ActualThemeChanged += (_, _) => UpdateCaptionButtons();
        UpdateCaptionButtons();
        RootFrame.Navigate(typeof(MainPage));
    }

    private void UpdateCaptionButtons()
    {
        AppWindow.TitleBar.ButtonForegroundColor = WindowRoot.ActualTheme == ElementTheme.Dark ? Colors.White : Colors.Black;
    }

    internal bool TryReportError(Exception exception)
    {
        if (RootFrame.Content is not MainPage page)
        {
            return false;
        }
        page.ReportUnexpectedError(exception);
        return true;
    }

    internal void HandleRedirectedActivation(string? scanPath)
    {
        // Bring a minimized window back, but leave a maximized one maximized.
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }
        Activate();

        if (!string.IsNullOrWhiteSpace(scanPath) && RootFrame.Content is MainPage page)
        {
            _ = page.StartExternalScanAsync(scanPath);
        }
    }
}
