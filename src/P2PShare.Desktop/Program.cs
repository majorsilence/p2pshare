using Majorsilence.Forms;
using P2PShare.Core;
using P2PShare.Core.Settings;

namespace P2PShare.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var settings = AppSettings.Load();
        var service = new P2PShareService(settings);
        _ = service.StartAsync();

        try
        {
            Application.Run(new MainForm(service));
        }
        finally
        {
            service.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
