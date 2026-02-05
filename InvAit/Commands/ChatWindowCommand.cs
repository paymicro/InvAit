using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using InvAit.ToolWindows;
using Microsoft.VisualStudio.Shell;

namespace InvAit.Commands;

[Command(PackageIds.ShowChatWindowCommand)]
public class ChatWindowCommand : BaseCommand<ChatWindowCommand>
{
    protected override Task ExecuteAsync(OleMenuCmdEventArgs e) => ChatToolWindow.ShowAsync();
}
