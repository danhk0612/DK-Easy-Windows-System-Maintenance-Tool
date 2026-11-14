using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace DK.Easy.WindowsMaintenance;

internal static class FileLocks
{
    internal sealed record Holder(int Pid, long StartedAt, string Name, bool CanClose)
    {
        public string Label => $"{Name} (PID {Pid})" + (CanClose ? "" : " · 직접 닫기 필요");
    }

    internal static Holder[] Find(string path)
    {
        Check(RmStartSession(out var session, 0, new StringBuilder(33)));
        try
        {
            Check(RmRegisterResources(session, 1, [path], 0, IntPtr.Zero, 0, IntPtr.Zero));
            uint count = 0;
            var result = RmGetList(session, out var needed, ref count, null, out _);
            for (var attempt = 0; result == 234 && attempt < 4; attempt++)
            {
                count = needed;
                var entries = new NativeProcessInfo[count];
                result = RmGetList(session, out needed, ref count, entries, out _);
                if (result == 0)
                {
                    using var current = Process.GetCurrentProcess();
                    return entries.Take((int)count).Select(entry => new Holder(entry.Process.Pid,
                        ((long)entry.Process.Start.dwHighDateTime << 32) | (uint)entry.Process.Start.dwLowDateTime,
                        string.IsNullOrWhiteSpace(entry.Name) ? "이름 없음" : entry.Name,
                        entry.Kind is 1 or 2 && entry.Session == current.SessionId
                        && entry.Process.Pid != current.Id && string.IsNullOrWhiteSpace(entry.Service))).ToArray();
                }
            }
            Check(result);
            return [];
        }
        finally { RmEndSession(session); }
    }

    internal static async Task<bool> RequestCloseAsync(string path, Holder selected)
    {
        if (!selected.CanClose || !Find(path).Contains(selected))
            return IsAvailable(path);
        try
        {
            using var process = Process.GetProcessById(selected.Pid);
            if (process.StartTime.ToFileTimeUtc() != selected.StartedAt || !process.CloseMainWindow())
                return IsAvailable(path);
            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (IsAvailable(path))
                    return true;
                await Task.Delay(300);
            }
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        return IsAvailable(path);
    }

    internal static bool IsAvailable(string path)
    {
        try
        {
            using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        {
            return false;
        }
    }

    private static void Check(int result)
    {
        if (result != 0)
            throw new Win32Exception(result);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeProcess
    {
        internal int Pid;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Start;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeProcessInfo
    {
        internal NativeProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string Service;
        internal uint Kind;
        internal uint Status;
        internal int Session;
        [MarshalAs(UnmanagedType.Bool)] internal bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, StringBuilder key);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint fileCount, string[] files,
        uint appCount, IntPtr applications, uint serviceCount, IntPtr services);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count,
        [In, Out] NativeProcessInfo[]? entries, out uint rebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);
}
