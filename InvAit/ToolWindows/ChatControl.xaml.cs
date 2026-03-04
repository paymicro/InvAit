using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using Community.VisualStudio.Toolkit;
using InvAit.Agent;
using InvAit.Utils;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.Web.WebView2.Core;
using Shared.Contracts;
using WV = Microsoft.Web.WebView2.Wpf;

namespace InvAit.ToolWindows;

public partial class ChatControl
{
    private bool _webView2Installed;
    private WV.IWebView2 _webView;
    private readonly ToolExecutor _toolExecutor;
    private bool _skipSslValidation;
    private string _vsVersion;

    public event Func<WV.IWebView2, Task> WebViewInitialized;
    public event Action UIReady;

    public ChatControl()
    {
        InitializeComponent();
        _toolExecutor = new ToolExecutor();
        Loaded += (_, _) => _ = HandleLoadedAsync();

        VSColorTheme.ThemeChanged += _ => UpdateTheme();
    }

    private void UpdateTheme()
    {
        if (!_webView2Installed)
        {
            return;
        }

        var bgColor = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundBrushKey);
        _webView?.DefaultBackgroundColor = bgColor;
        var isDarkTheme = bgColor.GetBrightness() <= 0.3; // яркость меньше 30% - темная тема
        _webView?.CoreWebView2.Profile.PreferredColorScheme = isDarkTheme ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
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

        _vsVersion ??= (await VS.Shell.GetVsVersionAsync()).ToString();
        WebViewHost.Content = _webView = new WV.WebView2();
        _webView.CoreWebView2InitializationCompleted += CoreWebView2InitializationCompleted;

        var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "InvAitChatWebView2");

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
                e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
                Logger.Log($"SSL validation error. You can skip SSL validation in settings.", "ERROR");
            }
        };
        _webView.CoreWebView2.ProcessFailed += (s, e) =>
        {
            Logger.Log($"WebView process failed: {e.ProcessFailedKind}", "ERROR");
            // При падении процесса WebView, нужно его перезагрузить.
            // Иначе он будет показывать только чистый экран.
            _webView.CoreWebView2.Reload();
        };
        _webView.CoreWebView2.NewWindowRequested += (s, e) =>
        {
            // Блокируем открытие новых окон. Все должно работать в одном окне.
            e.Handled = true;
            Logger.Log($"Blocked attempt to open new window with URL: {e.Uri}", "WARNING");
        };
    }

    /// <summary>
    /// Если окно чата в TabControl то клавиши Home и End перехватываются TabControl студии и не попадают в webView.
    /// Для этого сделан этот перехватчик и симуляция отправки клавиш через JS-скрипт
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Home || e.Key == Key.End)
        {
            // Прерываем маршрутизацию события на уровне WPF.
            // Если фокус на WebView, это событие сработает первым.
            e.Handled = true;

            // Отправляем команду в WebView через JS
            // Это имитирует стандартное поведение клавиш Home/End
            var isShiftDown = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);

            // Вызываем нашу JS функцию
            _ = _webView.ExecuteScriptAsync($"window.handleNavigationKey('{e.Key}', {isShiftDown.ToString().ToLower()});");
        }
        base.OnPreviewKeyDown(e);
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
        _webView.CoreWebView2.Settings.AreDevToolsEnabled = true; // не страшно, мы ж опен-сурс =)
        _webView.CoreWebView2.Settings.AreHostObjectsAllowed = true;
        _webView.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
        _webView.CoreWebView2.Settings.IsGeneralAutofillEnabled = true;
        _webView.CoreWebView2.Settings.IsBuiltInErrorPageEnabled = true;
        _webView.CoreWebView2.Settings.IsScriptEnabled = true;
        _webView.CoreWebView2.Settings.IsZoomControlEnabled = true;
        _webView.CoreWebView2.Settings.IsWebMessageEnabled = true;
        _webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = true;
        _webView.CoreWebView2.Settings.UserAgent = $"VisualStudio/{_vsVersion} ({Vsix.Name}/{Vsix.Version})";

        UpdateTheme();

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
            await Logger.LogAsync($"Wrong source {e.Source}", "ERROR");
            return;
        }

        try
        {
            var request = JsonSerializer.Deserialize<VsRequest>(e.WebMessageAsJson, JsonSerializerOptions.Web);
            if (request == null)
            {
                return;
            }

            if (request.Action == BasicEnum.SkipSSL)
            {
                _skipSslValidation = string.Equals(request.Payload, "true", StringComparison.OrdinalIgnoreCase);
                return;
            }

            if (request.Action == BasicEnum.UIReady)
            {
                UIReady?.Invoke();
                return;
            }

            await Logger.LogAsync($"WebMessage {request.Action} received.");
            var response = await _toolExecutor.ExecuteAsync(request);
            var json = new { type = nameof(VsResponse), payload = response };
            _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(json, JsonSerializerOptions.Web));
            await Logger.LogAsync($"WebMessage {request.Action} response result: {response.Success}.");
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

        // virtualHost ссылается на папку UI
        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            virtualHost,
            blazorRoot,
            CoreWebView2HostResourceAccessKind.Allow);

        _webView.Source = new Uri($"https://{virtualHost}/index.html");
    }
}
