using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace DK.Easy.WindowsMaintenance;

internal static class MaintenanceScan
{
    internal static SystemOverview ReadOverview()
    {
        using var windows = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var version = Environment.OSVersion.Version;
        var release = windows?.GetValue("DisplayVersion") as string;
        var revision = windows?.GetValue("UBR") as int?;
        var edition = windows?.GetValue("EditionID") as string;
        var os = $"Windows 11 {edition} {release}".Trim();
        var build = revision is int ubr ? $"{version.Build}.{ubr}" : $"{version.Build}";
        var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
        var total = drive.TotalSize;
        var free = drive.TotalFreeSpace;
        return new SystemOverview(Environment.MachineName, $"{os} · 빌드 {build}", drive.Name, total, free);
    }

    internal static EventSnapshot ReadSystemEvents(int days, bool includeWarnings, CancellationToken token)
    {
        const int limit = 200;
        var through = DateTimeOffset.UtcNow;
        var since = through.AddDays(-days);
        var levels = includeWarnings ? "Level=1 or Level=2 or Level=3" : "Level=1 or Level=2";
        var start = since.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var end = through.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var time = $"TimeCreated[@SystemTime >= '{start}' and @SystemTime <= '{end}']";
        var system = ReadChannel("System", $"*[System[({levels}) and {time}]]", limit, token);
        var application = ReadChannel("Application",
            $"*[System[(({levels}) or (Provider[@Name='Windows Error Reporting'] and EventID=1001)) and {time}]]", limit, token);
        var newest = system.Concat(application).OrderByDescending(entry => entry.OccurredAt)
            .Take(limit + 1).ToArray();
        return new EventSnapshot(since, through, includeWarnings, newest.Length > limit,
            newest.Take(limit).ToArray());
    }

    private static List<SystemEvent> ReadChannel(string channel, string xpath, int limit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var query = new EventLogQuery(channel, PathType.LogName, xpath) { ReverseDirection = true };
        using var reader = new EventLogReader(query);
        var entries = new List<SystemEvent>();
        while (entries.Count <= limit)
        {
            token.ThrowIfCancellationRequested();
            using var record = reader.ReadEvent();
            if (record is null)
                break;

            string? message;
            try
            {
                message = record.FormatDescription();
            }
            catch (EventLogException)
            {
                message = null;
            }

            var severity = record.Level switch { 1 => "심각", 2 => "오류", 3 => "경고", _ => "보고" };
            entries.Add(new SystemEvent(record.TimeCreated, channel, severity, record.ProviderName, record.Id,
                record.RecordId, message));
        }
        return entries;
    }
}

internal sealed record SystemOverview(string Computer, string Windows, string Drive, long Total, long Free);
internal sealed record SystemEvent(DateTime? OccurredAt, string Channel, string Severity, string? Provider, int EventId,
    long? RecordId, string? Message)
{
    public string DisplayTime => OccurredAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "시각 없음";
}

internal sealed record EventSnapshot(DateTimeOffset SinceUtc, DateTimeOffset ThroughUtc,
    bool IncludesWarnings, bool IsTruncated, IReadOnlyList<SystemEvent> Entries);
