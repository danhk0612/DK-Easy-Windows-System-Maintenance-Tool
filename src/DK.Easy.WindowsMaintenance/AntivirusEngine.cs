using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DK.Easy.WindowsMaintenance;

internal static class AntivirusEngine
{
    private const string ReleaseEndpoint = "https://api.github.com/repos/Cisco-Talos/clamav/releases/latest";
    private const string DownloadRoot = "https://github.com/Cisco-Talos/clamav/releases/download/";
    internal static string Root { get; } = Path.Combine(AppContext.BaseDirectory, "ClamAV");
    private static readonly string Database = Path.Combine(Root, "database");
    private static readonly string CurrentFile = Path.Combine(Root, "current.txt");
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(3) };

    static AntivirusEngine() => Client.DefaultRequestHeaders.UserAgent.ParseAdd("DK-Easy-Windows-Maintenance-Tool");

    private static bool ValidTag(string tag) => Regex.IsMatch(tag, @"\Aclamav-[0-9]+\.[0-9]+\.[0-9]+\z");

    internal static string? CurrentVersion()
    {
        string tag;
        try { tag = File.ReadAllText(CurrentFile).Trim(); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        return ValidTag(tag) && File.Exists(Path.Combine(Root, tag, "clamscan.exe"))
            && File.Exists(Path.Combine(Root, tag, "freshclam.exe")) ? tag : null;
    }

    internal static async Task<string> UpdateAsync(IProgress<string> progress, CancellationToken token)
    {
        using var operationLock = AcquireLock();
        var current = CurrentVersion();
        using var metadata = JsonDocument.Parse(await Client.GetStringAsync(ReleaseEndpoint, token));
        var release = metadata.RootElement;
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!ValidTag(tag))
            throw new IOException("공식 릴리스 버전 형식을 확인할 수 없습니다.");

        var name = tag + ".win.x64.zip";
        var asset = release.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(item => item.GetProperty("name").GetString() == name);
        if (asset.ValueKind == JsonValueKind.Undefined || !asset.TryGetProperty("digest", out var digestValue)
            || digestValue.GetString() is not string digest || !Regex.IsMatch(digest, @"\Asha256:[0-9a-fA-F]{64}\z"))
            throw new IOException("공식 Windows x64 ZIP 또는 SHA-256 정보를 찾지 못했습니다.");

        using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        downloadTimeout.CancelAfter(TimeSpan.FromMinutes(20));
        var staging = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        var installation = Path.Combine(Root, tag);
        try
        {
            var engine = installation;
            if (current != tag)
            {
                Directory.CreateDirectory(staging);
                var archive = Path.Combine(staging, name);
                progress.Report("ClamAV 엔진 ZIP 다운로드 및 검증 중…");
                await using (var file = File.Create(archive))
                using (var response = await Client.GetAsync(DownloadRoot + tag + "/" + name,
                    HttpCompletionOption.ResponseHeadersRead, downloadTimeout.Token))
                {
                    response.EnsureSuccessStatusCode();
                    await response.Content.CopyToAsync(file, downloadTimeout.Token);
                }
                await using (var file = File.OpenRead(archive))
                {
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
                    if (!string.Equals(hash, digest[7..], StringComparison.OrdinalIgnoreCase))
                        throw new IOException("다운로드한 엔진 ZIP의 SHA-256이 공식 릴리스와 다릅니다.");
                }
                var extraction = Path.Combine(staging, "extracted");
                ZipFile.ExtractToDirectory(archive, extraction);
                engine = Path.Combine(extraction, tag + ".win.x64");
                if (!File.Exists(Path.Combine(engine, "clamscan.exe"))
                    || !File.Exists(Path.Combine(engine, "freshclam.exe")))
                    throw new IOException("압축 파일에 필요한 ClamAV 실행 파일이 없습니다.");
            }

            token.ThrowIfCancellationRequested();
            progress.Report("악성코드 정의 다운로드 / 업데이트 중…");
            Directory.CreateDirectory(Database);
            var config = Path.Combine(Root, "freshclam.conf");
            File.WriteAllText(config, "DatabaseMirror database.clamav.net\n");
            var result = await RunAsync(engine, "freshclam.exe", token,
                "--config-file=" + config, "--datadir=" + Database);
            if (result.ExitCode != 0 || (!File.Exists(Path.Combine(Database, "daily.cvd"))
                && !File.Exists(Path.Combine(Database, "daily.cld"))))
                throw new IOException($"악성코드 정의 업데이트 실패 ({result.ExitCode})\n{result.Output}");

            if (engine != installation)
            {
                token.ThrowIfCancellationRequested();
                if (Directory.Exists(installation))
                    Directory.Delete(installation, recursive: true);
                Directory.Move(engine, installation);
                var pointer = Path.Combine(staging, "current.txt");
                File.WriteAllText(pointer, tag);
                File.Move(pointer, CurrentFile, overwrite: true);
                if (current is not null)
                    DeleteTemporaryDirectory(Path.Combine(Root, current));
            }
            return result.Output;
        }
        finally
        {
            DeleteTemporaryDirectory(staging);
        }
    }

    internal static async Task<(int ExitCode, string Output, long Processed)> ScanAsync(
        string target, long previousCount, IProgress<(long Done, long Total)> progress, CancellationToken token)
    {
        using var operationLock = AcquireLock();
        var current = CurrentVersion() ?? throw new IOException("먼저 ClamAV 엔진을 다운로드하세요.");
        var fullPath = Path.GetFullPath(target);
        for (string? part = fullPath; part is not null; part = Path.GetDirectoryName(part))
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("연결 경로는 검사 대상에 지정할 수 없습니다.");
        var total = Directory.Exists(fullPath) ? previousCount : 1;
        progress.Report((0, total));
        var completed = 0L;
        var clock = Stopwatch.StartNew();
        void ReportFile()
        {
            completed++;
            if (clock.ElapsedMilliseconds < 200)
                return;
            clock.Restart();
            progress.Report((completed, total));
        }

        var result = await RunProcessAsync(Path.Combine(Root, current), "clamscan.exe", token, ReportFile,
            "--recursive", "--cross-fs=no", "--follow-dir-symlinks=0", "--follow-file-symlinks=0",
            "--official-db-only=yes", "--database=" + Database, "--", fullPath);
        return (result.ExitCode, result.Output, completed);
    }

    private static FileStream AcquireLock()
    {
        try { Directory.CreateDirectory(Root); }
        catch (UnauthorizedAccessException error)
        {
            throw new IOException($"엔진 폴더에 쓸 수 없습니다: {Root}", error);
        }
        try { return File.Open(Path.Combine(Root, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new IOException("ClamAV 작업을 시작할 수 없습니다. 다른 앱 창에서 실행 중인지 확인하세요.", error); }
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static Task<(int ExitCode, string Output)> RunAsync(
        string engine, string program, CancellationToken token, params string[] arguments) =>
        RunProcessAsync(engine, program, token, null, arguments);

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string engine, string program, CancellationToken token, Action? fileProcessed, params string[] arguments)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(Path.Combine(engine, program))
        {
            WorkingDirectory = engine, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new IOException("ClamAV를 실행할 수 없습니다.");
        using var cancel = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        var output = fileProcessed is null ? process.StandardOutput.ReadToEndAsync()
            : ReadScanOutputAsync(process.StandardOutput, fileProcessed);
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        token.ThrowIfCancellationRequested();
        return (process.ExitCode, string.Join(Environment.NewLine, await output, await errors).Trim());
    }

    private static async Task<string> ReadScanOutputAsync(StreamReader reader, Action fileProcessed)
    {
        var report = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.EndsWith(": OK", StringComparison.Ordinal) || line.EndsWith(": Empty file", StringComparison.Ordinal))
                fileProcessed();
            else
            {
                if (line.EndsWith(" FOUND", StringComparison.Ordinal) || line.EndsWith(" ERROR", StringComparison.Ordinal))
                    fileProcessed();
                report.AppendLine(line);
            }
        }
        return report.ToString();
    }
}
