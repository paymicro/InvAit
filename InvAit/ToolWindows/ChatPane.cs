using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;

namespace InvAit.ToolWindows;

[Guid("80d3f8d0-296d-496c-ac69-7b55fc04d2e2")]
public class ChatPane : ToolWindowPane
{
    public ChatPane()
    {
        BitmapImageMoniker = KnownMonikers.InfoTipInline;
    }
}
