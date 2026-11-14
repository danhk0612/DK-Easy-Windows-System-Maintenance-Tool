using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace DK.Easy.WindowsMaintenance;

public partial class MainWindow
{
    private CancellationTokenSource? antivirusCancellation;
    private string? antivirusTargetPath;
    private bool InspectionBusy => scanCancellation is not null || antivirusCancellation is not null || managing;

    private void RefreshInspectionButtons()
    {
        var available = !InspectionBusy;
        ScanButton.IsEnabled = available;
        OverviewScanButton.IsEnabled = available;
        InspectPageButton.IsEnabled = available;
        EventScanButton.IsEnabled = available;
        if (trayScan is not null)
            trayScan.Enabled = available;
        RefreshAntivirus();
        UpdateInventorySelection();
    }

    private void RefreshAntivirus()
    {
        string? installed = null;
        try
        {
            installed = AntivirusEngine.CurrentVersion();
            AntivirusState.Text = installed is null
                ? $"ClamAV 엔진 미설치 · 다운로드 위치: {AntivirusEngine.Root}"
                : $"ClamAV {installed["clamav-".Length..]} · 선택한 위치를 검사하고 결과를 표시합니다.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AntivirusState.Text = $"엔진 상태 조회 실패 · {error.Message}";
        }

        var available = !InspectionBusy;
        InstallEngineButton.IsEnabled = available;
        AntivirusTree.IsEnabled = available;
        AntivirusScanButton.IsEnabled = available && installed is not null && antivirusTargetPath is not null;
    }

    private void SelectAntivirusTarget(string path)
    {
        if (InspectionBusy)
            return;
        if (AntivirusTree.SelectedItem is TreeViewItem selected && !Equals(selected.Tag, path))
            selected.IsSelected = false;
        antivirusTargetPath = path;
        AntivirusTarget.Text = path;
        NavigationList.SelectedItem = NavigationList.Items.OfType<ListBoxItem>().Single(item => Equals(item.Tag, "antivirus"));
        RefreshAntivirus();
    }

    private void InitializeAntivirusTree()
    {
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.DriveType is DriveType.Fixed or DriveType.Removable))
            AntivirusTree.Items.Add(AntivirusNode(drive.Name, drive.Name, true));
    }

    private TreeViewItem AntivirusNode(string name, string path, bool folder)
    {
        var node = new TreeViewItem { Header = name, Tag = path };
        if (folder)
        {
            node.Items.Add(new TreeViewItem { Header = "폴더 열기", IsEnabled = false });
            node.Expanded += AntivirusNode_Expanded;
        }
        return node;
    }

    private async void AntivirusNode_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem node || e.OriginalSource != node)
            return;
        node.Expanded -= AntivirusNode_Expanded;
        var path = (string)node.Tag;
        node.Items.Clear();
        node.Items.Add(new TreeViewItem { Header = "불러오는 중…", IsEnabled = false });
        try
        {
            var entries = await Task.Run(() => new DirectoryInfo(path).EnumerateFileSystemInfos()
                .Where(entry => !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .OrderBy(entry => entry is not DirectoryInfo)
                .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(entry => (entry.Name, entry.FullName, Folder: entry is DirectoryInfo)).ToArray());
            node.Items.Clear();
            foreach (var entry in entries)
                node.Items.Add(AntivirusNode(entry.Name, entry.FullName, entry.Folder));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            node.Items.Clear();
            node.Items.Add(new TreeViewItem { Header = "폴더를 열 수 없습니다.", IsEnabled = false, ToolTip = error.Message });
            node.Expanded += AntivirusNode_Expanded;
        }
    }

    private void AntivirusTree_SelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem { Tag: string path })
            SelectAntivirusTarget(path);
    }

    private async void InstallEngine_Click(object sender, RoutedEventArgs e) =>
        await RunAntivirusAsync(null);

    private async void AntivirusScan_Click(object sender, RoutedEventArgs e)
    {
        if (antivirusTargetPath is { } target)
            await RunAntivirusAsync(target);
    }

    private async Task RunAntivirusAsync(string? target)
    {
        if (InspectionBusy)
            return;

        using var cancellation = new CancellationTokenSource();
        antivirusCancellation = cancellation;
        RefreshInspectionButtons();
        CancelScanButton.IsEnabled = true;
        CancelScanButton.Visibility = Visibility.Visible;
        ScanProgress.IsIndeterminate = true;
        ScanProgress.Visibility = Visibility.Visible;
        StatusText.Text = target is null ? "ClamAV 릴리스 확인 중…" : "악성코드 검사 중…";
        try
        {
            if (target is null)
            {
                var progress = new Progress<string>(message =>
                {
                    if (antivirusCancellation == cancellation && !cancellation.IsCancellationRequested)
                        StatusText.Text = message;
                });
                await Task.Run(() => AntivirusEngine.UpdateAsync(progress, cancellation.Token));
                StatusText.Text = "ClamAV 엔진 및 악성코드 정의 준비 완료";
            }
            else
            {
                ScanProgress.Maximum = 100;
                ScanProgress.Value = 0;
                StatusText.Text = "바이러스 검사 · 엔진 준비 중…";
                var previousCount = lastAntivirusReport is { } previous
                    && string.Equals(previous.Target, target, StringComparison.OrdinalIgnoreCase) ? previous.Processed : 0;
                var progress = new Progress<(long Done, long Total)>(value =>
                {
                    if (antivirusCancellation != cancellation || cancellation.IsCancellationRequested)
                        return;
                    ScanProgress.IsIndeterminate = value.Total == 0;
                    ScanProgress.Value = value.Total == 0 ? 0 : Math.Min(99, 100.0 * value.Done / value.Total);
                    StatusText.Text = value.Total == 0 ? $"바이러스 검사 · 처리 {value.Done:N0}개 파일" :
                        $"바이러스 검사 · 약 {ScanProgress.Value:0.#}% · 처리 {value.Done:N0}개 파일";
                });
                var result = await Task.Run(() => AntivirusEngine.ScanAsync(target, previousCount, progress, cancellation.Token));
                string? cacheError = null;
                if (result.ExitCode is 0 or 1)
                {
                    var report = new AntivirusReport(target, result.Output, result.ExitCode, result.Processed);
                    lastAntivirusReport = report;
                    ShowAntivirusReport(report);
                    cacheError = await SaveScanResultAsync("antivirus", report, DateTimeOffset.Now);
                    RefreshScanTime();
                }
                else
                {
                    AntivirusOutput.AppendText("\n\n최근 검사 실패\n" + result.Output);
                }
                StatusText.Text = result.ExitCode switch
                {
                    0 => "바이러스 검사 완료 · 100% · 탐지 없음 · 검사 범위와 오류는 결과에서 확인",
                    1 => "바이러스 검사 완료 · 100% · 악성코드 탐지 · 결과에서 파일과 탐지명을 확인하세요.",
                    _ => $"악성코드 검사 실패 · 종료 코드 {result.ExitCode}"
                };
                if (cacheError is not null)
                    StatusText.Text += $" · 기록 저장 실패: {cacheError}";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = cancellation.IsCancellationRequested ? "ClamAV 작업 취소됨" : "ClamAV 서버 연결 시간 초과";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException
            or Win32Exception or JsonException or InvalidDataException or InvalidOperationException or ArgumentException
            or System.Collections.Generic.KeyNotFoundException)
        {
            AntivirusOutput.AppendText("\n\n" + error.Message);
            StatusText.Text = target is null ? "ClamAV 다운로드 / 업데이트 실패" : "악성코드 검사 실패";
        }
        finally
        {
            antivirusCancellation = null;
            CancelScanButton.Visibility = Visibility.Collapsed;
            ScanProgress.IsIndeterminate = false;
            ScanProgress.Visibility = Visibility.Collapsed;
            RefreshInspectionButtons();
        }
    }
}
