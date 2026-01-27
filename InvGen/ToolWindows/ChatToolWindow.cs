using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Community.VisualStudio.Toolkit;
using EnvDTE;
using EnvDTE80;
using InvGen.Agent;
using Microsoft.VisualStudio.Shell;
using Microsoft.Web.WebView2.Wpf;
using Shell = Microsoft.VisualStudio.Shell;

namespace InvGen.ToolWindows;

public class ChatToolWindow : BaseToolWindow<ChatToolWindow>, IDisposable
{
    private VsCodeContextPublisher _contextPublisher;
    
    public override string GetTitle(int toolWindowId) => "InvGen Chat";

    public override Type PaneType => typeof(ChatPane);

    public override async Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var control = new ChatControl();
        control.WebViewInitialized += OnWebViewInitialized;

        return control;
    }

    private async Task OnWebViewInitialized(IWebView2 webView)
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
