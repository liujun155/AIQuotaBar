using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AIQuotaBar.App.Interop;

// Windows 11's XAML taskbar can consume input before an embedded WPF child sees it.
internal sealed class TaskbarMouseInput : IDisposable
{
    private readonly Window _window;
    private readonly IntPtr _handle;
    private readonly HookProc _callback;
    private IntPtr _hook;

    public TaskbarMouseInput(Window window, Action click, Action rightClick, Action<int> wheel)
    {
        _window = window;
        _handle = new WindowInteropHelper(window).Handle;
        _callback = (code, message, data) =>
        {
            var kind = message.ToInt32();
            if (code >= 0 && kind is 0x0201 or 0x0202 or 0x0204 or 0x0205 or 0x020A &&
                NativeMethods.IsWindowVisible(_handle))
            {
                var mouse = Marshal.PtrToStructure<MouseData>(data);
                if (NativeMethods.GetWindowRect(_handle, out var rect) &&
                    mouse.Point.X >= rect.Left && mouse.Point.X < rect.Right &&
                    mouse.Point.Y >= rect.Top && mouse.Point.Y < rect.Bottom)
                {
                    var root = GetAncestor(WindowFromPoint(mouse.Point), 2);
                    if (root == _handle || root == GetAncestor(_handle, 2))
                    {
                        Action? action = kind switch
                        {
                            0x0202 => click,
                            0x0205 => rightClick,
                            0x020A => () => wheel(unchecked((short)(mouse.Data >> 16))),
                            _ => null,
                        };
                        if (action != null) _window.Dispatcher.BeginInvoke(action);
                        return new IntPtr(1); // Prevent duplicate WPF input and clicks on Explorer underneath.
                    }
                }
            }
            return CallNextHookEx(_hook, code, message, data);
        };
        _hook = SetWindowsHookEx(14, _callback, IntPtr.Zero, 0);
        if (_hook == IntPtr.Zero)
            System.Diagnostics.Trace.WriteLine($"Taskbar mouse hook unavailable: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseData
    {
        public NativeMethods.POINT Point;
        public uint Data, Flags, Time;
        public IntPtr ExtraInfo;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativeMethods.POINT point);
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);
}
