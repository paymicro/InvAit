using System;
using System.Configuration.Assemblies;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
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
        Loaded += OnLoaded;

        _viewModel = (ChatViewModel)DataContext;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
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

        _webView = new WebView2();
        //_webView.CoreWebView2InitializationCompleted += CoreWebView2InitializationCompleted;
        //_webView.NavigationCompleted += (_, _) =>
        //{
        //    SafeExecuteJs(WebFunctions.ReloadThemeCss(WebAsset.IsDarkTheme));
        //    _viewModel.ForceDownloadChats();
        //    _ = _viewModel.LoadChatAsync();
        //};

        WebViewHost.Content = _webView;

        var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "InvGenChatWebView2");

        var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
        await _webView.EnsureCoreWebView2Async(env);

        SetupVirtualHost();

        //_webView.WebMessageReceived += WebViewOnWebMessageReceived;

        _webView.CoreWebView2.NavigationStarting += (sender, args) =>
        {
            SetupVirtualHost();
        };

    }

    private void SetupVirtualHost()
    {
        var virtualHost = "blazorui.local";
        var assemblyPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        var blazorRoot = Path.Combine(assemblyPath, "UI");

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
