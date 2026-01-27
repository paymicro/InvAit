using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using InvGen.Agent;
using InvGen.Utils;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.Web.WebView2.Core;
using Shared.Contracts;
using WV = Microsoft.Web.WebView2.Wpf;

namespace InvGen.ToolWindows;

public partial class ChatControl
{
    private bool _webView2Installed;
    private WV.IWebView2 _webView;
    private readonly BuiltInAgent _builtInAgent;
    private bool _skipSslValidation;

    public event Func<WV.IWebView2, Task> WebViewInitialized;

    public ChatControl()
    {
        InitializeComponent();
        _builtInAgent = new BuiltInAgent();
        Loaded += (_, _) => _ = HandleLoadedAsync();
        PreviewKeyDown += ChatControl_PreviewKeyDown;
    }

    private void ChatControl_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // фикс обработки Home или End
        if (e.Key is Key.Home or Key.End && _webView2Installed)
        {
            e.Handled = true;

            // Создаём JavaScript-код для эмуляции нажатия клавиши в браузере
            var jsCode = e.Key == Key.Home
                ? "document.activeElement.setSelectionRange(0, 0); document.activeElement.scrollIntoView(true);"
                : "var len = document.activeElement.value.length; document.activeElement.setSelectionRange(len, len); document.activeElement.scrollIntoView(false);";

            // Выполняем JavaScript в WebView2
            _ = _webView.ExecuteScriptAsync(jsCode);
        }
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

        WebViewHost.Content = _webView = new WV.WebView2();

        _webView.CoreWebView2InitializationCompleted += CoreWebView2InitializationCompleted;

        var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "InvGenChatWebView2");

        // Включаем CORS выключаем защиту
        var options = new CoreWebView2EnvironmentOptions("--allow-running-insecure-content --disable-web-security --disable-features=BlockInsecurePrivateNetworkRequests");
        var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
        await _webView.EnsureCoreWebView2Async(env);

        SetupVirtualHost();

        _webView.WebMessageReceived += (sender, e) => _ = HandleWebMessageAsync(e);
        _webView.CoreWebView2.NavigationStarting += (_, _) => SetupVirtualHost();
        _webView.CoreWebView2.ServerCertificateErrorDetected += (s, e) =>
        {
            if (_skipSslValidation)
            {
                e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
                Logger.Log($"SSL validation is skipped.");
            }
            else
            {
                Logger.Log($"SSL validation error. You can skip SSL validation in settings.", "ERROR");
            }
        };

        //_webView.CoreWebView2.AddHostObjectToScript("mcpHost", new McpProxyHost());
        //_webView.NavigationCompleted += (_, _) =>
        //{
        //    SafeExecuteJs(WebFunctions.ReloadThemeCss(WebAsset.IsDarkTheme));
        //    _viewModel.ForceDownloadChats();
        //    _ = _viewModel.LoadChatAsync();
        //};
    }

    private void CoreWebView2InitializationCompleted(object sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            Debug.WriteLine($"WebView error: {e.InitializationException}");
            return;
        }
        _webView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = true;
        _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
#if !DEBUG
        _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
#else
        _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
#endif
        _webView.CoreWebView2.Settings.AreHostObjectsAllowed = true;
        _webView.CoreWebView2.Settings.IsPasswordAutosaveEnabled = true;
        _webView.CoreWebView2.Settings.IsGeneralAutofillEnabled = true;
        _webView.CoreWebView2.Settings.IsBuiltInErrorPageEnabled = true;
        _webView.CoreWebView2.Settings.IsScriptEnabled = true;
        _webView.CoreWebView2.Settings.IsZoomControlEnabled = true;
        _webView.CoreWebView2.Settings.IsWebMessageEnabled = true;
        _webView.DefaultBackgroundColor = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundBrushKey);
        _webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = true;
        _webView.CoreWebView2.Settings.AreHostObjectsAllowed = true;
        _webView.CoreWebView2.Settings.UserAgent = "InvGen VisualStudio plugin";
        try
        {
            _webView.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WebView MemoryUsageTargetLevel error: {ex}");
        }
        
        WebViewInitialized?.Invoke(_webView).FireAndForget();
    }

    private async Task HandleWebMessageAsync(CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (e.Source != _webView.Source.ToString())
        {
            // Ожидаем вызов тулзов только из правильных мест.
            // Чтобы левые сайты (если будет поиск в вебе) не могли получить доступ к тулзам.
            // И поиск браузером должен быть на стороне UI, т.к. там и так браузер.
            Logger.Log($"Wrong source {e.Source}", "ERROR");
            return;
        }

        try
        {
            var request = JsonSerializer.Deserialize<VsRequest>(e.WebMessageAsJson, JsonSerializerOptions.Web);
            if (request == null)
            {
                return;
            }

            if (request.Action == "SkipSSL")
            {
                _skipSslValidation = string.Equals(request.Payload, "true", StringComparison.OrdinalIgnoreCase);
                Logger.Log($"SkipSSL set {_skipSslValidation}");
                return;
            }

            Logger.Log($"WebMessage {request.Action} received.");
            var response = await _builtInAgent.ExecuteAsync(request);
            var json = new { type = nameof(VsResponse), payload = response };
            _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(json, JsonSerializerOptions.Web));
            Logger.Log($"WebMessage {request.Action} response result: {response.Success}.");
        }
        catch (Exception ex)
        {
            // По идее сюда не должно дойди.
            await Logger.LogAsync($"WebMessage error: {ex.Message}", "FATAL");
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
