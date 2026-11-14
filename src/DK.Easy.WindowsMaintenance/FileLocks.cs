using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DK.Easy.WindowsMaintenance;

internal static class FileLocks
{
    internal sealed record Holder(int Pid, long StartedAt, string Name, bool CanClose)
    {
        public string Label => $"{Name} (PID {Pid})" + (CanClose ? "" : " · 직접 닫기 필요");
    }

    internal sealed record SearchResult(Holder[] Holders, int LockedFiles, int Skipped);

    internal static Holder[] Find(string path) => FindFiles([path]);

    private static Holder[] FindFiles(string[] paths)
    {
        Check(RmStartSession(out var session, 0, new StringBuilder(33)));
        try
        {
            Check(RmRegisterResources(session, (uint)paths.Length, paths, 0, IntPtr.Zero, 0, IntPtr.Zero));
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

    internal static SearchResult Search(string path, CancellationToken token)
    {
        path = Path.GetFullPath(path);
        for (string? parent = path; parent is not null; parent = Path.GetDirectoryName(parent))
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("연결 경로는 검사할 수 없습니다.");

        token.ThrowIfCancellationRequested();
        if ((File.GetAttributes(path) & FileAttributes.Directory) == 0)
            return IsAvailable(path) ? new([], 0, 0) : new(Find(path), 1, 0);

        var holders = new Dictionary<(int Pid, long StartedAt), Holder>();
        var pending = new Stack<string>();
        var batch = new List<string>(32);
        var locked = 0;
        var skipped = 0;
        pending.Push(path);

        void CollectHolders()
        {
            if (batch.Count == 0)
                return;
            token.ThrowIfCancellationRequested();
            try
            {
                foreach (var holder in FindFiles(batch.ToArray()))
                    holders.TryAdd((holder.Pid, holder.StartedAt), holder);
            }
            catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3 or 5)
            {
                skipped += batch.Count;
            }
            batch.Clear();
        }

        while (pending.TryPop(out var folder))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                {
                    skipped++;
                    continue;
                }
                foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                            skipped++;
                        else if (entry is DirectoryInfo)
                            pending.Push(entry.FullName);
                        else if (!IsAvailable(entry.FullName))
                        {
                            locked++;
                            batch.Add(entry.FullName);
                            if (batch.Count == 32)
                                CollectHolders();
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        skipped++;
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }
        CollectHolders();
        return new(holders.Values.OrderBy(holder => holder.Name).ThenBy(holder => holder.Pid).ToArray(), locked, skipped);
    }

    internal static bool RequestClose(Holder selected)
    {
        if (!selected.CanClose)
            return false;
        try
        {
            using var process = Process.GetProcessById(selected.Pid);
            return process.StartTime.ToFileTimeUtc() == selected.StartedAt && process.CloseMainWindow();
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
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

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RmStartSession(out uint session, int flags, StringBuilder key);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RmRegisterResources(uint session, uint fileCount, string[] files,
        uint appCount, IntPtr applications, uint serviceCount, IntPtr services);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count,
        [In, Out] NativeProcessInfo[]? entries, out uint rebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);
}
