using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Shared.Contracts;
using MessageBox = System.Windows.MessageBox;
using Package = Microsoft.VisualStudio.Shell.Package;

namespace InvGen.ToolWindows;

public partial class ChatControl
{
    private bool _webView2Installed;
    private readonly ChatViewModel _viewModel;
    private IWebView2 _webView;

    public ChatControl()
    {
        InitializeComponent();
        _viewModel = (ChatViewModel)DataContext;
        Loaded += (_, _) => _ = HandleLoadedAsync();
    }

    private async Task HandleLoadedAsync()
    {
        try
        {
            if (_webView?.CoreWebView2 != null)
            {
                _ = _webView.CoreWebView2.BrowserProcessId;
                return;
            }
        }
        catch (Exception)
        {
            WebViewHost.Content = null;
            _webView?.Dispose();
            _webView = null;
        }

        if (!_webView2Installed)
        {
            //_webView2Installed = await WebView2BootstrapperHelper.EnsureRuntimeAvailableAsync();
            _webView2Installed = true;

            if (!_webView2Installed)
            {
                return;
            }
        }

        WebViewHost.Content = _webView = new WebView2();

        var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "InvGenChatWebView2");

        var options = new CoreWebView2EnvironmentOptions("--allow-running-insecure-content");
        var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
        await _webView.EnsureCoreWebView2Async(env);

        SetupVirtualHost();

        _webView.WebMessageReceived += (sender, e) => _ = HandleWebMessageAsync(e);
        _webView.CoreWebView2.NavigationStarting += (_, _) => SetupVirtualHost();
        //_webView.CoreWebView2InitializationCompleted += CoreWebView2InitializationCompleted;
        //_webView.NavigationCompleted += (_, _) =>
        //{
        //    SafeExecuteJs(WebFunctions.ReloadThemeCss(WebAsset.IsDarkTheme));
        //    _viewModel.ForceDownloadChats();
        //    _ = _viewModel.LoadChatAsync();
        //};
    }

    private async Task HandleWebMessageAsync(CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var request = JsonSerializer.Deserialize<VsRequest>(e.WebMessageAsJson);
            // test
            MessageBox.Show(e.WebMessageAsJson);

            if (request == null)
            {
                return;
            }

            VsResponse response = request.Action switch
            {
                //"getActiveDocumentContent" => await HandleGetDocumentAsync(request),
                //"insertTextAtCursor" => await HandleInsertTextAsync(request),
                _ => new VsResponse { CorrelationId = request.CorrelationId, Success = false, Error = "Unknown action" }
            };

            var json = JsonSerializer.Serialize(response);
            await _webView.CoreWebView2.ExecuteScriptAsync($"window.receiveVsResponse({json})");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WebMessage error: {ex.Message}");
        }
    }

    private void SetupVirtualHost()
    {
        var virtualHost = "blazorui.local";
        var assemblyPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        var blazorRoot = Path.Combine(assemblyPath, "UI", "wwwroot");

#if DEBUG
        if (!Directory.Exists(blazorRoot))
        {
            throw new DirectoryNotFoundException($"Blazor publish folder not found. Expected: {blazorRoot}. Make sure you copied wwwroot after dotnet publish.");
        }
#endif

        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            virtualHost,
            blazorRoot,
            CoreWebView2HostResourceAccessKind.Allow);

        _webView.Source = new Uri($"https://{virtualHost}/index.html");
    }
}
