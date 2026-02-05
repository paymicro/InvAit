using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using InvAit.ToolWindows;
using InvAit.Utils;
using Microsoft.VisualStudio.Shell;

namespace InvAit;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration(Vsix.Name, Vsix.Description, Vsix.Version)]
[Guid(PackageGuids.InvAitPackageString)]
[ProvideToolWindow(typeof(ChatPane), Style = VsDockStyle.Tabbed, DockedWidth = 300, Window = WindowGuids.DocumentWell, Orientation = ToolWindowOrientation.Right)]
[ProvideMenuResource("Menus.ctmenu", 1)]
public sealed class InvAitPackage : ToolkitPackage
{
    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        this.RegisterToolWindows();

        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        await this.RegisterCommandsAsync();

        var test = JsonUtils.DeserializeParameters("{\"regex\": \" ^.*? main_services.* \"}");
        await Logger.InitializeAsync();
        await Logger.LogAsync("Started");
    }
}
