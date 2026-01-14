using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using InvGen.ToolWindows;
using InvGen.Utils;
using Microsoft.VisualStudio.Shell;

namespace InvGen;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration(Vsix.Name, Vsix.Description, Vsix.Version)]
[Guid(PackageGuids.InvGenPackageString)]
[ProvideToolWindow(typeof(ChatPane), Style = VsDockStyle.Tabbed, DockedWidth = 300, Window = WindowGuids.DocumentWell, Orientation = ToolWindowOrientation.Right)]
[ProvideMenuResource("Menus.ctmenu", 1)]
public sealed class InvGenPackage : ToolkitPackage
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