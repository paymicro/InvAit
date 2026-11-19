using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
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

    /// <summary>
    /// Minimum distance between two automatic renderer reloads. Static on purpose: the tool
    /// window (and therefore this control) can be recreated while a crashed renderer is still
    /// in its failure loop, so the throttle must outlive a single control instance.
    /// </summary>
    private static readonly TimeSpan AutoReloadMinInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// <see cref="Stopwatch.GetTimestamp"/> of the last automatic reload, or 0 when none has
    /// happened yet. Written through <see cref="Interlocked"/> because it is shared by every
    /// control instance; the event itself is raised on the thread that owns the WebView.
    /// </summary>
    private static long _lastAutoReloadTimestamp;
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
        var options = new CoreWebView2EnvironmentOptions(
            "--allow-running-insecure-content " +
            "--disable-web-security " +
            "--disable-background-networking " +
            "--disable-component-update " +
            "--disable-translation " +
            "--disable-features=BlockInsecurePrivateNetworkRequests")
        {
            IsCustomCrashReportingEnabled = true // Блокирует отправку Crash Dumps в Microsoft
        };
        var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
        try
        {
            await _webView.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            await Logger.LogAsync($"Failed to launch WebView. To completely wipe data, delete the folder '{userDataFolder}' and restart the application. " +
                $"Details: {ex.Message}", "ERROR");
            throw;
        }

        SetupVirtualHost();

        _webView.WebMessageReceived += (sender, e) => _ = HandleWebMessageAsync(e);
        _webView.CoreWebView2.WebResourceRequested += CoreWebView2WebResourceRequested;
        _webView.CoreWebView2.Settings.IsReputationCheckingRequired = false; // Блокирует отправку Microsoft SmartScreen
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
        _webView.CoreWebView2.ProcessFailed += (s, e) => HandleProcessFailed(e);
        _webView.CoreWebView2.NewWindowRequested += (s, e) =>
        {
            // Блокируем открытие новых окон. Все должно работать в одном окне.
            e.Handled = true;
            // В дефолтном браузере
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri,
                UseShellExecute = true
            });
        };
        _webView.CoreWebView2.NavigationStarting += (s, e) =>
        {
            var url = e.Uri;
            // Условие: если это не ваш локальный контент или не стартовая страница
            if (url != _virtualUrl)
            {
                e.Cancel = true;
                // В дефолтном браузере
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
        };
    }

    /// <summary>
    /// Recovers from a WebView2 process failure by reloading the renderer - but at most ONE
    /// automatic reload per <see cref="AutoReloadMinInterval"/>.
    /// <para>
    /// WHY the reload is rate-limited: a renderer that became unresponsive (a heavy session with
    /// hundreds of messages blocks the single WASM thread) fails, an unconditional reload runs
    /// the very same page load again, the renderer dies again. That is an endless flicker loop
    /// which never surfaces a diagnostic and keeps burning CPU. After a second failure inside the
    /// window we therefore only log an actionable ERROR line and leave the decision (reload the
    /// tool window / restart Visual Studio) to the user; nothing is lost because sessions are
    /// persisted before the renderer is involved.
    /// </para>
    /// <para>
    /// The throttle is stamped only after a reload was actually issued, so a renderer that stays
    /// healthy afterwards is never penalised by an old failure, and a throwing Reload() leaves the
    /// window open for a retry. This handler never throws: an exception escaping a crash callback
    /// would take the host down with it.
    /// </para>
    /// </summary>
    private void HandleProcessFailed(CoreWebView2ProcessFailedEventArgs e)
    {
        // ProcessFailedKind is the only diagnostic WebView2 exposes here; the exit code/reason of
        // RenderProcessExited is not surfaced through this event. The SDK assembly version is
        // enough to correlate a report with the installed WebView2 build.
        var diagnostics = $"kind: {e.ProcessFailedKind}, WebView2 SDK: {typeof(CoreWebView2).Assembly.GetName().Version}";

        var now = Stopwatch.GetTimestamp();
        var lastReload = Interlocked.Read(ref _lastAutoReloadTimestamp);
        if (lastReload != 0)
        {
            var sinceLastReload = TimeSpan.FromSeconds((now - lastReload) / (double)Stopwatch.Frequency);
            if (sinceLastReload < AutoReloadMinInterval)
            {
                Logger.Log(
                    $"WebView process failed again ({diagnostics}) {sinceLastReload.TotalSeconds:F1}s after the previous automatic reload. " +
                    $"The renderer is most likely stuck on the same heavy page, so the UI is NOT reloaded automatically " +
                    $"(at most one reload per {AutoReloadMinInterval.TotalSeconds:F0}s) to avoid an endless reload loop. " +
                    "Reload the tool window or restart Visual Studio to recover; the session on disk is intact.",
                    "ERROR");
                return;
            }
        }

        Logger.Log(
            $"WebView process failed ({diagnostics}). Reloading the renderer " +
            $"(automatic reloads are throttled to one per {AutoReloadMinInterval.TotalSeconds:F0}s).",
            "ERROR");

        try
        {
            _webView?.CoreWebView2?.Reload();

            // Stamped AFTER the reload was issued: the window starts at the reload, so a session
            // that stays healthy is not penalised by a failure it already recovered from.
            Interlocked.Exchange(ref _lastAutoReloadTimestamp, now);
        }
        catch (Exception ex)
        {
            // A throwing Reload() must not escape a crash callback; the timestamp stays untouched,
            // so the next failure is still allowed to try again.
            Logger.Log($"WebView reload after a process failure failed: {ex.Message}", "ERROR");
        }
    }

    /// <summary>
    /// Блокировка хождения в интернет и любой фоновой телеметрии Chromium движка
    /// </summary>
    private void CoreWebView2WebResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!e.Request.Uri.StartsWith(_virtualUrl, StringComparison.OrdinalIgnoreCase))
        {
            // Жестко блокируем запрос, возвращая "Доступ запрещен"
            e.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(null, 403, "Forbidden", "Content-Type: text/plain");
        }
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

            if (request.Action == BasicEnum.LogError)
            {
                // fire-and-forget: UI -> host log, без отправки VsResponse.
                if (string.IsNullOrEmpty(request.Payload))
                {
                    return;
                }

                try
                {
                    var entry = JsonSerializer.Deserialize<LogEntry>(request.Payload, JsonSerializerOptions.Web);
                    if (entry == null)
                    {
                        return;
                    }

                    // Logger сам добавляет префикс [timestamp] [level] — не дублируем уровень в message.
                    var level = entry.Level == LogEntry.ErrorLevel ? "ERROR"
                        : entry.Level == LogEntry.WarnLevel ? "WARN"
                        : "INFO";
                    var message = $"[{entry.Source}] {entry.Message}";
                    if (!string.IsNullOrEmpty(entry.Exception))
                    {
                        message += "\n" + entry.Exception;
                    }

                    await Logger.LogAsync(message, level);
                }
                catch (JsonException)
                {
                    // Битый payload от UI — молча игнорируем, ответ не нужен.
                }

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

    private static readonly string _virtualHost = "blazorui.local";
    private static readonly string _virtualUrl = $"https://{_virtualHost}/index.html";

    private void SetupVirtualHost()
    {
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
            _virtualHost,
            blazorRoot,
            CoreWebView2HostResourceAccessKind.Allow);

        _webView.Source = new Uri(_virtualUrl);
    }
}
