using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ChallengeLab.App.Controls.Aether;

/// <summary>Finds the MSFS main window client rectangle in screen coordinates.</summary>
internal sealed class AetherSimViewport
{
    private static readonly string[] ProcessNames =
    [
        "FlightSimulator2024",
        "FlightSimulator",
    ];

    public bool TryGetClientBounds(out AetherClientBounds bounds)
    {
        bounds = default;
        foreach (var name in ProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch
            {
                continue;
            }

            try
            {
                foreach (var process in processes)
                {
                    var handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero || !IsWindowVisible(handle) || IsIconic(handle))
                        continue;

                    if (!GetClientRect(handle, out var client))
                        continue;

                    var width = client.Right - client.Left;
                    var height = client.Bottom - client.Top;
                    if (width < 240 || height < 180)
                        continue;

                    var origin = new NativePoint { X = 0, Y = 0 };
                    if (!ClientToScreen(handle, ref origin))
                        continue;

                    bounds = new AetherClientBounds(origin.X, origin.Y, width, height);
                    return true;
                }
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }

        return false;
    }

    internal readonly record struct AetherClientBounds(int Left, int Top, int Width, int Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);
}
