using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace DiskLoom;

public sealed partial class MainWindow : Window
{
    private const uint WmDeviceChange = 0x0219;
    private const nuint DbtDeviceArrival = 0x8000;
    private const nuint DbtDeviceRemoveComplete = 0x8004;
    private const nuint SubclassId = 1;

    private readonly SubclassProc _subclassProc;
    private readonly nint _windowHandle;
    private DispatcherQueueTimer? _deviceChangeTimer;

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

        _windowHandle = WindowNative.GetWindowHandle(this);
        _subclassProc = WindowProc;
        SetWindowSubclass(_windowHandle, _subclassProc, SubclassId, 0);
        Closed += (_, _) => RemoveWindowSubclass(_windowHandle, _subclassProc, SubclassId);

        RootFrame.Navigate(typeof(MainPage));
    }

    private nint WindowProc(nint hwnd, uint msg, nuint wParam, nint lParam, nuint subclassId, nuint refData)
    {
        if (msg == WmDeviceChange && wParam is DbtDeviceArrival or DbtDeviceRemoveComplete)
        {
            ScheduleDriveRefresh();
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private void ScheduleDriveRefresh()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_deviceChangeTimer is null)
            {
                _deviceChangeTimer = DispatcherQueue.CreateTimer();
                _deviceChangeTimer.Interval = TimeSpan.FromMilliseconds(300);
                _deviceChangeTimer.IsRepeating = false;
                _deviceChangeTimer.Tick += (_, _) =>
                {
                    if (RootFrame.Content is MainPage page)
                    {
                        page.RefreshDrives();
                    }
                };
            }
            _deviceChangeTimer.Stop();
            _deviceChangeTimer.Start();
        });
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

    private delegate nint SubclassProc(nint hwnd, uint msg, nuint wParam, nint lParam, nuint subclassId, nuint refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nuint wParam, nint lParam);
}
