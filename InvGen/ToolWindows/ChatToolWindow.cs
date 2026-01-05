using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;

namespace InvGen.ToolWindows;

public class ChatToolWindow : BaseToolWindow<ChatToolWindow>
{
    public override string GetTitle(int toolWindowId) => "InvGen Chat";

    public override Type PaneType => typeof(ChatPane);

    public override async Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var control = new ChatControl();

        return control;
    }
}
