using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace DK.Easy.WindowsMaintenance;

internal sealed record StorageEntry(string Path, string Kind, long Bytes, DateTime ModifiedUtc)
{
    public string Size => FormatSize(Bytes);

    internal static string FormatSize(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        var value = (double)bytes;
        var unit = 0;
        while (Math.Abs(value) >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }
    public DateTime Modified => ModifiedUtc.ToLocalTime();
}

internal sealed record StorageResult(IReadOnlyList<StorageEntry> Large, IReadOnlyList<StorageEntry> Temporary,
    long TempBytes, int TempCount, int Skipped);

internal static class StorageScan
{
    private const long GiB = 1024L * 1024 * 1024;
    private const int DisplayLimit = 300;
    internal static string UserTemp => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");

    internal static StorageResult Run(string root, int fileGiB, int folderGiB, CancellationToken token)
    {
        var large = new PriorityQueue<StorageEntry, long>();
        var temporary = new PriorityQueue<StorageEntry, long>();
        var skipped = 0;
        var tempCount = 0;
        var tempBytes = 0L;

        long Visit(DirectoryInfo directory, bool temp, bool includeDirectory)
        {
            token.ThrowIfCancellationRequested();
            long bytes = 0;
            try
            {
                if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    skipped++;
                    return 0;
                }

                foreach (var entry in directory.EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            skipped++;
                            continue;
                        }

                        if (entry is DirectoryInfo child)
                        {
                            bytes += Visit(child, temp, !temp);
                            continue;
                        }

                        var file = (FileInfo)entry;
                        var length = file.Length;
                        bytes += length;
                        if (temp)
                        {
                            tempBytes += length;
                            tempCount++;
                            Add(temporary, new StorageEntry(file.FullName, "임시파일", length, file.LastWriteTimeUtc));
                        }
                        else if (length >= fileGiB * GiB)
                        {
                            Add(large, new StorageEntry(file.FullName, "파일", length, file.LastWriteTimeUtc));
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        skipped++;
                    }
                }

                if (includeDirectory && bytes >= folderGiB * GiB)
                    Add(large, new StorageEntry(directory.FullName, "폴더", bytes, directory.LastWriteTimeUtc));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
            return bytes;
        }

        Visit(new DirectoryInfo(root), false, false);
        if (Directory.Exists(UserTemp) && !string.IsNullOrWhiteSpace(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)))
            Visit(new DirectoryInfo(UserTemp), true, false);
        return new StorageResult(Sort(large), Sort(temporary), tempBytes, tempCount, skipped);
    }

    private static void Add(PriorityQueue<StorageEntry, long> entries, StorageEntry entry)
    {
        entries.Enqueue(entry, entry.Bytes);
        if (entries.Count > DisplayLimit)
            entries.Dequeue();
    }

    private static StorageEntry[] Sort(PriorityQueue<StorageEntry, long> entries) =>
        entries.UnorderedItems.Select(item => item.Element).OrderByDescending(item => item.Bytes).ToArray();
}
