using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Community.VisualStudio.Toolkit;
using EnvDTE;
using EnvDTE80;
using InvAit.Agent;
using Microsoft.VisualStudio.Shell;
using Microsoft.Web.WebView2.Wpf;
using Shell = Microsoft.VisualStudio.Shell;

namespace InvAit.ToolWindows;

public class ChatToolWindow : BaseToolWindow<ChatToolWindow>, IDisposable
{
    private VsCodeContextPublisher _contextPublisher;

    public override string GetTitle(int toolWindowId) => "InvAit Chat";

    public override Type PaneType => typeof(ChatPane);

    public override async Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
    {
        await Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var control = new ChatControl();
        control.WebViewInitialized += OnWebViewInitializedAsync;
        control.UIReady += () => _contextPublisher?.PushInitialContextAsync().FireAndForget();

        return control;
    }

    private async Task OnWebViewInitializedAsync(IWebView2 webView)
    {
        var dte = Shell.Package.GetGlobalService(typeof(DTE)) as DTE2;
        if (dte != null)
        {
            _contextPublisher = await VsCodeContextPublisher.CreateAsync(dte, (WebView2)webView);
        }
    }

    public void Dispose()
    {
        _contextPublisher?.Dispose();
        _contextPublisher = null;
    }
}
