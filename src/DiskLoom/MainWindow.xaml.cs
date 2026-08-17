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
}
