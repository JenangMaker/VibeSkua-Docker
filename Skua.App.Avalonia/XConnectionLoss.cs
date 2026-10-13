using System.Runtime.InteropServices;

namespace Skua.App.Avalonia;

/// <summary>
/// Quits at once when the X server goes away. libX11's own handler prints
/// "X connection to :1 broken" and calls exit(), whose cleanup then runs while
/// the .NET runtime's other threads keep going: the process segfaulted and left
/// a core file in /config (seen when a container stop took the X server down
/// before a tab's Skua). Nothing can be drawn or saved without X anyway, so
/// skip that cleanup with _exit. The container's stop hook
/// (s6-rc.d/vibeskua-stop) stops Skua before X, so this is only the safety net
/// for X dying on its own.
/// </summary>
public static class XConnectionLoss
{
    private delegate int IoErrorHandler(IntPtr display);

    // Kept alive for as long as libX11 holds the pointer: the whole process.
    private static readonly IoErrorHandler Handler = OnLost;

    public static void Install()
    {
        try
        {
            XSetIOErrorHandler(Handler);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    private static int OnLost(IntPtr display)
    {
        try
        {
            Console.Error.WriteLine("[host] lost the X server; exiting");
            Console.Error.Flush();
        }
        catch
        {
        }
        _exit(1);
        return 0;
    }

    [DllImport("libX11.so.6")]
    private static extern IntPtr XSetIOErrorHandler(IoErrorHandler handler);

    [DllImport("libc")]
    private static extern void _exit(int status);
}
