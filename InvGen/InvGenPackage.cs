using Community.VisualStudio.Toolkit;
using InvGen.ToolWindows;
using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

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
    }
}