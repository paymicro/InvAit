using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using InvGen.ToolWindows;
using Microsoft.VisualStudio.Shell;

namespace InvGen.Commands;

[Command(PackageIds.ShowChatWindowCommand)]
public class ChatWindowCommand : BaseCommand<ChatWindowCommand>
{
    protected override Task ExecuteAsync(OleMenuCmdEventArgs e) => ChatToolWindow.ShowAsync();
}
