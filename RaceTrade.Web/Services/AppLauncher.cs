using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RaceTrade.Web.Services;

/// <summary>
/// Desktop-app behaviour for what is technically a web server: open the UI in the default
/// browser once Kestrel is listening, then get the console window out of the way so the
/// thing behaves like the old WinForms build (double-click, window appears, no terminal).
/// </summary>
internal static class AppLauncher
{
    /// <summary>Opens <paramref name="url"/> in the system default browser.</summary>
    public static void OpenBrowser(string url)
    {
        try
        {
            // UseShellExecute is what makes the OS pick the default handler. Without it,
            // .NET Core tries to exec the URL as a program and throws.
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsLinux())
            {
                Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open", url) { UseShellExecute = false });
            }
        }
        catch
        {
            // Headless box, no xdg-open, locked-down shell - none of that is fatal.
            // The URL is printed to the console anyway.
        }
    }

    /// <summary>
    /// Detaches from the console window, which closes it when we were the only owner
    /// (the double-click case).
    ///
    /// Uses GetConsoleProcessList first: if another process is attached, the console is
    /// somebody's terminal (cmd, Windows Terminal, an SSH session) and detaching from it
    /// would silently swallow every later line of output - including the port-in-use
    /// message. Only a console we own by ourselves gets closed.
    ///
    /// No-op on Linux/macOS: there is no separate console window there, the process just
    /// inherits the terminal that launched it.
    /// </summary>
    public static void DetachConsole()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            var buffer = new uint[4];
            var count = GetConsoleProcessList(buffer, (uint)buffer.Length);

            // 0 means no console at all (already a GUI process); >1 means we are sharing
            // someone else's terminal. Neither should be touched.
            if (count != 1) return;

            Console.Out.Flush();

            // MUST happen before FreeConsole.
            //
            // Console.Out/Error/In are cached streams wrapping the console handles. Once
            // FreeConsole runs those handles are dead, and the NEXT Console.WriteLine
            // anywhere in the process throws IOException("The handle is invalid") — which
            // is exactly what killed the Start button: SiteConfigManager writes a
            // "Loaded and cached configuration" line while loading site configs, so the
            // exception surfaced as "Failed to start the racer: The handle is invalid".
            //
            // Swapping in the null writers first means later console output is discarded
            // instead of throwing. Everything worth seeing already goes to the UI log.
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            Console.SetIn(TextReader.Null);

            FreeConsole();
        }
        catch
        {
            // Console APIs missing (unlikely) - just keep the window.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);
}
