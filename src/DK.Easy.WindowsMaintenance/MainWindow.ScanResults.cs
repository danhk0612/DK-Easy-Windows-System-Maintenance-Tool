using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace DK.Easy.WindowsMaintenance;

public partial class MainWindow
{
    private sealed record SavedScan<T>(DateTimeOffset When, T Result);
    private sealed record AntivirusReport(string Target, string Output, int ExitCode, long Processed);
    private static readonly string ScanResultsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DKEasyWindowsMaintenance", "last-scans");
    private readonly Dictionary<string, DateTimeOffset> scanTimes = new();
    private readonly HashSet<string> restoredScanKeys = new();
    private AntivirusReport? lastAntivirusReport;

    private static string StorageCacheKey(string drive) => "storage-" + char.ToUpperInvariant(drive[0]);
    private string ActiveScanKey => activePage == "storage" && DriveSelect.SelectedItem is string drive
        ? StorageCacheKey(drive) : activePage;

    private void RestoreScanResults()
    {
        void Restore<T>(string key, Action<T> apply)
        {
            try
            {
                var path = Path.Combine(ScanResultsFolder, key + ".json");
                if (!File.Exists(path))
                    return;
                var saved = JsonSerializer.Deserialize<SavedScan<T>>(File.ReadAllText(path));
                if (saved is null || saved.Result is null)
                    return;
                apply(saved.Result);
                scanTimes[key] = saved.When;
                restoredScanKeys.Add(key);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                StatusText.Text = "일부 이전 검사 결과를 읽을 수 없습니다.";
            }
        }

        Restore<SystemOverview>("dashboard", ShowOverview);
        Restore<EventSnapshot>("events", ShowEvents);
        ExportButton.IsEnabled = events is not null;
        foreach (var drive in (IEnumerable<string>)DriveSelect.ItemsSource)
            Restore<StorageResult>(StorageCacheKey(drive), result => storageResults[drive] = result);
        foreach (var page in new[] { "startup", "services", "tasks", "apps" })
            Restore<InventoryEntry[]>(page, result => inventoryResults[page] = result);
        Restore<AntivirusReport>("antivirus", report =>
        {
            lastAntivirusReport = report;
            ShowAntivirusReport(report);
        });
        RefreshOverviewCounts();
    }

    private async Task<string?> SaveScanResultAsync<T>(string key, T result, DateTimeOffset when)
    {
        scanTimes[key] = when;
        restoredScanKeys.Remove(key);
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(ScanResultsFolder);
                var path = Path.Combine(ScanResultsFolder, key + ".json");
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, JsonSerializer.Serialize(new SavedScan<T>(when, result)));
                    File.Move(temporary, path, overwrite: true);
                }
                finally
                {
                    File.Delete(temporary);
                }
            });
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return error.Message;
        }
    }

    private void RefreshScanTime()
    {
        PageScanTime.Visibility = activePage is "settings" or "history" or "unlock" ? Visibility.Collapsed : Visibility.Visible;
        var key = ActiveScanKey;
        PageScanTime.Text = scanTimes.TryGetValue(key, out var when)
            ? $"마지막 검사 {when.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                + (restoredScanKeys.Contains(key) ? " · 저장된 결과" : "")
            : "마지막 검사 없음";
        if (inventoryErrors.TryGetValue(activePage, out var error))
            PageScanTime.Text += $" · 최근 조회 실패: {error}";
    }

    private void ShowEvents(EventSnapshot snapshot)
    {
        events = snapshot;
        EventsGrid.ItemsSource = snapshot.Entries;
        EventSummary.Text = $"{snapshot.SinceUtc.ToLocalTime():yyyy-MM-dd HH:mm} ~ {snapshot.ThroughUtc.ToLocalTime():yyyy-MM-dd HH:mm}"
            + $" · {snapshot.Entries.Count:N0}건"
            + (snapshot.IncludesWarnings ? " · 경고 포함" : " · 심각·오류 및 문제 보고")
            + (snapshot.IsTruncated ? " · 최신 200건만 표시" : "");
    }

    private void ShowAntivirusReport(AntivirusReport report) =>
        AntivirusOutput.Text = $"검사 대상: {report.Target}\n"
            + (report.ExitCode == 1 ? "악성코드 탐지" : "탐지 없음 · 검사 범위와 오류는 결과에서 확인")
            + "\n\n" + report.Output;
}
