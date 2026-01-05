using Community.VisualStudio.Toolkit;
using InvGen.ToolWindows;
using Microsoft.VisualStudio.Shell;
using System.Threading.Tasks;

namespace InvGen.Commands;

[Command(PackageIds.ShowChatWindowCommand)]
public class ChatWindowCommand : BaseCommand<ChatWindowCommand>
{
    protected override Task ExecuteAsync(OleMenuCmdEventArgs e) => ChatToolWindow.ShowAsync();
}
