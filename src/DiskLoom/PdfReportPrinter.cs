using Microsoft.Web.WebView2.Core;

namespace DiskLoom;

// Renders the HTML report in an invisible WebView2 and prints it to PDF, so the PDF gets the
// same layout, fonts and full Unicode file names as the HTML report. Must run on the UI thread.
internal static class PdfReportPrinter
{
    public static async Task PrintAsync(string htmlPath, string pdfPath, string title, nint windowHandle)
    {
        // The default user-data folder sits next to the executable, which is read-only under Program Files.
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiskLoom",
            "WebView2");
        Directory.CreateDirectory(userDataFolder);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var environment = await CoreWebView2Environment.CreateWithOptionsAsync(string.Empty, userDataFolder, new CoreWebView2EnvironmentOptions())
            .AsTask(timeout.Token).WaitAsync(timeout.Token);
        var controller = await environment.CreateCoreWebView2ControllerAsync(
            CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)windowHandle)).AsTask(timeout.Token).WaitAsync(timeout.Token);
        try
        {
            controller.IsVisible = false;
            var webView = controller.CoreWebView2;
            var navigation = new TaskCompletionSource<CoreWebView2WebErrorStatus?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            webView.ProcessFailed += (_, _) => failed.TrySetResult();
            webView.NavigationCompleted += (_, args) => navigation.TrySetResult(args.IsSuccess ? null : args.WebErrorStatus);
            webView.Navigate(new Uri(htmlPath).AbsoluteUri);
            if (await Task.WhenAny(navigation.Task, failed.Task).WaitAsync(timeout.Token) == failed.Task)
                throw new IOException("The PDF renderer stopped unexpectedly.");
            if (await navigation.Task is { } error)
            {
                throw new InvalidOperationException($"The report could not be rendered ({error}).");
            }

            var settings = environment.CreatePrintSettings();
            settings.Orientation = CoreWebView2PrintOrientation.Portrait;
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintHeaderAndFooter = true;
            settings.HeaderTitle = title;
            settings.FooterUri = string.Empty;
            settings.MarginTop = 0.5;
            settings.MarginBottom = 0.5;
            settings.MarginLeft = 0.4;
            settings.MarginRight = 0.4;
            var printing = webView.PrintToPdfAsync(pdfPath, settings).AsTask(timeout.Token);
            if (await Task.WhenAny(printing, failed.Task).WaitAsync(timeout.Token) == failed.Task)
                throw new IOException("The PDF renderer stopped unexpectedly.");
            if (!await printing)
            {
                throw new IOException($"The PDF could not be written to {pdfPath}.");
            }
        }
        finally
        {
            controller.Close();
        }
    }
}
