using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace DK.Easy.WindowsMaintenance;

internal sealed record InventoryEntry(string Id, string Name, string Location, string State,
    string? Publisher, string? Detail, bool SystemItem = false, string? Target = null,
    string? Version = null, string? InstallDate = null, long? EstimatedKiB = null, string? TaskDefinitionHash = null,
    DateTime? StartupFileModifiedUtc = null);

internal static class InventoryScan
{
    internal static IReadOnlyList<InventoryEntry> Startup(CancellationToken token)
    {
        var results = new List<InventoryEntry>();
        foreach (var directory in new[] { MaintenanceActions.UserStartup,
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), MaintenanceActions.DisabledStartup })
        {
            if (!Directory.Exists(directory))
                continue;
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                token.ThrowIfCancellationRequested();
                if (Path.GetFileName(path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    continue;
                var info = new FileInfo(path);
                var disabledFolder = directory == MaintenanceActions.DisabledStartup;
                var id = disabledFolder ? Path.Combine(MaintenanceActions.UserStartup, info.Name) : path;
                DateTime? modified = null;
                try
                {
                    if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) == 0)
                        modified = info.LastWriteTimeUtc;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                results.Add(new InventoryEntry(id, info.Name, directory, disabledFolder ? "비활성" : "등록됨",
                    null, path, Target: path, StartupFileModifiedUtc: modified));
            }
        }

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        foreach (var keyName in new[] { "Run", "RunOnce" })
        {
            token.ThrowIfCancellationRequested();
            if (hive == RegistryHive.CurrentUser && view == RegistryView.Registry32)
                continue;
            const string parent = @"Software\Microsoft\Windows\CurrentVersion\";
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(parent + keyName);
            if (key is null)
                continue;
            var location = $"{hive} · {view} · {keyName}";
            foreach (var name in key.GetValueNames())
            {
                var command = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                var target = ExecutablePath(command);
                var state = keyName == "RunOnce" ? "한 번 실행 · 조회 전용" :
                    target is null ? "실행 대상 확인 불가" :
                    File.Exists(target) ? "등록됨 · 대상 확인" : "대상 존재 여부 확인 필요";
                results.Add(new InventoryEntry($"{location}:{name}", name, location, state,
                    Publisher(target), command, Target: target));
            }
        }
        foreach (var scope in new[] { (RegistryHive.CurrentUser, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32) })
        {
            using var hive = RegistryKey.OpenBaseKey(scope.Item1, scope.Item2);
            using var disabled = hive.OpenSubKey(MaintenanceActions.DisabledRunKey);
            var location = $"{scope.Item1} · {scope.Item2}";
            foreach (var name in disabled?.GetValueNames() ?? [])
            {
                token.ThrowIfCancellationRequested();
                var command = disabled!.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                var target = ExecutablePath(command);
                results.Add(new InventoryEntry($"{location} · Run:{name}", name,
                    $"{location} · 비활성", "비활성", Publisher(target), command, Target: target));
            }
        }
        return results.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    internal static IReadOnlyList<InventoryEntry> InstalledApps(CancellationToken token)
    {
        var results = new List<InventoryEntry>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var root = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (root is null)
                continue;
            foreach (var name in root.GetSubKeyNames())
            {
                token.ThrowIfCancellationRequested();
                using var key = root.OpenSubKey(name);
                if (key?.GetValue("DisplayName") is not string displayName || key.GetValue("SystemComponent") is 1)
                    continue;
                var target = key.GetValue("InstallLocation") as string;
                var version = key.GetValue("DisplayVersion") as string;
                results.Add(new InventoryEntry($"{hive}:{view}:{name}", displayName,
                    string.IsNullOrWhiteSpace(target) ? $"{hive} · {view}" : target, version ?? "버전 없음",
                    key.GetValue("Publisher") as string, key.GetValue("UninstallString") as string,
                    Target: target, Version: version, InstallDate: key.GetValue("InstallDate") as string,
                    EstimatedKiB: key.GetValue("EstimatedSize") is int size && size >= 0 ? size : null));
            }
        }
        return results.DistinctBy(item => (item.Name, item.Version, item.Publisher, item.Target, item.Detail))
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    internal static async Task<IReadOnlyList<InventoryEntry>> Services(CancellationToken token)
    {
        const string script = """
            $rows = @(Get-CimInstance Win32_Service | ForEach-Object {
                [ordered]@{ Id=$_.Name; Name=$_.DisplayName; Location=$_.Name;
                    State=($_.State + ' / ' + $_.StartMode); Detail=$_.PathName; Publisher=$null }
            })
            ConvertTo-Json -InputObject $rows -Compress
            """;
        var rows = await ReadWindowsInventory(script, token);
        return rows.Select(row =>
        {
            var target = ExecutablePath(row.Detail);
            var publisher = Publisher(target);
            var system = target is not null && IsWithin(target, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            return row with { Target = target, Publisher = publisher, SystemItem = system };
        }).ToArray();
    }

    internal static Task<InventoryEntry[]> Tasks(CancellationToken token)
    {
        const string script = """
            $sha = [Security.Cryptography.SHA256]::Create()
            $rows = @(Get-ScheduledTask | ForEach-Object {
                $hash = $null
                if ($_.Author -and !$_.TaskPath.StartsWith('\Microsoft\', [StringComparison]::OrdinalIgnoreCase)) {
                    try {
                        $xml = Export-ScheduledTask -TaskName $_.TaskName -TaskPath $_.TaskPath
                        $hash = [Convert]::ToBase64String($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($xml)))
                    } catch { }
                }
                $action = ($_.Actions | ForEach-Object {
                    if ($_.Execute) { "$($_.Execute) $($_.Arguments)".Trim() }
                    elseif ($_.ClassId) { "COM: $($_.ClassId)" }
                    else { $_.CimClass.CimClassName }
                }) -join [Environment]::NewLine
                $trigger = ($_.Triggers | ForEach-Object {
                    "$($_.CimClass.CimClassName): $($_.StartBoundary)".TrimEnd(' ', ':')
                }) -join ', '
                [ordered]@{ Id=($_.TaskPath + $_.TaskName); Name=$_.TaskName; Location=$_.TaskPath;
                    State=[string]$_.State; Publisher=$_.Author; Detail=("$action" + [Environment]::NewLine + "트리거: $trigger");
                    TaskDefinitionHash=$hash; SystemItem=$_.TaskPath.StartsWith('\Microsoft\', [StringComparison]::OrdinalIgnoreCase) }
            })
            ConvertTo-Json -InputObject $rows -Compress
            """;
        return ReadWindowsInventory(script, token);
    }

    private static async Task<InventoryEntry[]> ReadWindowsInventory(string script, CancellationToken token)
    {
        var output = await RunWindowsPowerShell(script, token);
        return JsonSerializer.Deserialize<InventoryEntry[]>(output) ?? throw new IOException("조회 결과가 비어 있습니다.");
    }

    internal static async Task<string> RunWindowsPowerShell(string script, CancellationToken token)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        var command = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.Encoding]::UTF8;" + Environment.NewLine + script;
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
        using var process = Process.Start(start) ?? throw new IOException("Windows PowerShell을 시작하지 못했습니다.");
        var output = process.StandardOutput.ReadToEndAsync(token);
        var errors = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0)
                throw new IOException((await errors).Trim());
            return await output;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw;
        }
    }

    internal static string? ExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;
        var text = Environment.ExpandEnvironmentVariables(command.Trim());
        if (text.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), text[@"\SystemRoot\".Length..]);
        var end = text[0] == '"' ? text.IndexOf('"', 1) : text.IndexOfAny([' ', '\t']);
        var path = text[0] == '"' ? (end > 0 ? text[1..end] : "") : (end < 0 ? text : text[..end]);
        return Path.IsPathFullyQualified(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    internal static bool IsWithin(string path, string root) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static string? Publisher(string? path)
    {
        if (!File.Exists(path))
            return null;
        try { return FileVersionInfo.GetVersionInfo(path!).CompanyName; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return null; }
    }
}
