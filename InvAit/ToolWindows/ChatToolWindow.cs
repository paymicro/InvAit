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

    private ChatControl _chatControl;

    public override string GetTitle(int toolWindowId) => "InvAit Chat";

    public override Type PaneType => typeof(ChatPane);

    public override async Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        _chatControl = new ChatControl();
        _chatControl.WebViewInitialized += OnWebViewInitializedAsync;
        _chatControl.UIReady += () => _contextPublisher?.PushInitialContextAsync().FireAndForget();

        return _chatControl;
    }

    private async Task OnWebViewInitializedAsync(IWebView2 webView)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (Shell.Package.GetGlobalService(typeof(DTE)) is DTE2 dte)
        {
            _contextPublisher = await VsCodeContextPublisher.CreateAsync(dte, (WebView2)webView);
        }
    }

    public void Dispose()
    {
        _contextPublisher?.Dispose();
        _contextPublisher = null;
        _chatControl?.Dispose();
        _chatControl = null;
    }
}
