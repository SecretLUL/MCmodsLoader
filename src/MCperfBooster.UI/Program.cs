using MCperfBooster.Core.Utils;
using MCperfBooster.UI.Interop;

namespace MCperfBooster.UI;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        // The shell folder picker needs an initialised apartment. STAThread normally
        // arranges this, but asking explicitly keeps it true regardless of host.
        Native.CoInitializeEx(0, Native.COINIT_APARTMENTTHREADED);

        // Before anything reads settings or the cached mod list: a build from before the
        // rename kept them under %LOCALAPPDATA%\MCmodsLoader.
        AppPaths.MigrateLegacyDataDirectory();

        try
        {
            return MainWindow.Run();
        }
        catch (Exception ex)
        {
            // A crash with no window and no console would otherwise be silent.
            Native.MessageBoxW(0, ex.ToString(), "MCperfBooster - Unexpected Error", Native.MB_OK | Native.MB_ICONERROR);
            return 1;
        }
    }
}
