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
using Microsoft.Win32;

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
                ? "ClamAV 엔진 미설치 · 처음 사용할 때 엔진과 악성코드 정의를 다운로드하세요."
                : $"ClamAV {installed["clamav-".Length..]} · 선택한 위치를 검사하고 결과를 표시합니다.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AntivirusState.Text = $"엔진 상태 조회 실패 · {error.Message}";
        }

        var available = !InspectionBusy;
        InstallEngineButton.IsEnabled = available;
        SelectDriveButton.IsEnabled = available;
        SelectFolderButton.IsEnabled = available;
        SelectFileButton.IsEnabled = available;
        AntivirusScanButton.IsEnabled = available && installed is not null && antivirusTargetPath is not null;
    }

    private void SelectAntivirusTarget(string path)
    {
        if (InspectionBusy)
            return;
        antivirusTargetPath = path;
        AntivirusTarget.Text = path;
        NavigationList.SelectedItem = NavigationList.Items.OfType<ListBoxItem>().Single(item => Equals(item.Tag, "antivirus"));
        RefreshAntivirus();
    }

    private void SelectDrive_Click(object sender, RoutedEventArgs e)
    {
        var drives = new ComboBox { ItemsSource = DriveSelect.ItemsSource, SelectedItem = DriveSelect.SelectedItem };
        var choose = new Button { Content = "선택", IsDefault = true, Margin = new Thickness(0, 8, 0, 0) };
        var cancel = new Button { Content = "취소", IsCancel = true, Margin = new Thickness(0, 6, 0, 0) };
        var panel = new StackPanel { Margin = new Thickness(16), Children = { drives, choose, cancel } };
        var dialog = new Window
        {
            Title = "검사할 드라이브", Owner = this, ShowInTaskbar = false, Width = 290,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, Content = panel
        };
        choose.IsEnabled = drives.SelectedItem is not null;
        drives.SelectionChanged += (_, _) => choose.IsEnabled = drives.SelectedItem is not null;
        choose.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() == true && drives.SelectedItem is string root)
            SelectAntivirusTarget(root);
    }

    private void SelectFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "검사할 폴더" };
        if (dialog.ShowDialog(this) == true)
            SelectAntivirusTarget(dialog.FolderName);
    }

    private void SelectFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "검사할 파일", Filter = "모든 파일 (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true)
            SelectAntivirusTarget(dialog.FileName);
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
        AntivirusOutput.Clear();
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
                AntivirusOutput.Text = await Task.Run(() => AntivirusEngine.UpdateAsync(progress, cancellation.Token));
                StatusText.Text = "ClamAV 엔진 및 악성코드 정의 준비 완료";
            }
            else
            {
                var result = await Task.Run(() => AntivirusEngine.ScanAsync(target, cancellation.Token));
                AntivirusOutput.Text = target + Environment.NewLine + Environment.NewLine + result.Output;
                StatusText.Text = result.ExitCode switch
                {
                    0 => "검사한 파일에서 악성코드 탐지 없음 · 검사 범위와 오류는 결과에서 확인",
                    1 => "악성코드 탐지 · 결과에서 파일과 탐지명을 확인하세요.",
                    _ => $"악성코드 검사 실패 · 종료 코드 {result.ExitCode}"
                };
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
            AntivirusOutput.Text = error.Message;
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
