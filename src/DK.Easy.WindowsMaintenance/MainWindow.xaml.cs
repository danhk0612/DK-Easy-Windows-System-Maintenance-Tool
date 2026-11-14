using System;
using System.ComponentModel;
using System.Diagnostics.Eventing.Reader;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace DK.Easy.WindowsMaintenance;

public partial class MainWindow : Window
{
    private static readonly int[] Periods = [1, 7, 14, 30];
    private MaintenanceSettings settings = new();
    private EventSnapshot? events;
    private CancellationTokenSource? scanCancellation;
    private Forms.NotifyIcon? tray;
    private Forms.ToolStripMenuItem? trayScan;
    private Icon? trayIcon;
    private bool exitRequested;

    public MainWindow()
    {
        InitializeComponent();
        NavigationList.SelectedIndex = 0;
        try
        {
            settings = MaintenanceSettings.Load();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            StatusText.Text = "설정을 읽을 수 없어 기본값을 사용합니다. 설정 화면에서 다시 저장할 수 있습니다.";
        }

        CloseSetting.SelectedIndex = (int)settings.CloseBehavior;
        MinimizeSetting.IsChecked = settings.MinimizeToTray;
        LaunchSetting.IsChecked = settings.CheckOnLaunch;
        WarningSetting.IsChecked = settings.IncludeWarnings;
        DaysSetting.SelectedIndex = Array.IndexOf(Periods, settings.EventDays);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= Window_Loaded;
        var resource = Application.GetResourceStream(new Uri("Assets/Maintenance.ico", UriKind.Relative));
        using (var stream = resource.Stream)
        using (var icon = new Icon(stream))
            trayIcon = (Icon)icon.Clone();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("창 열기", null, (_, _) => RestoreWindow());
        trayScan = new Forms.ToolStripMenuItem("전체 검사", null, async (_, _) => await RunScanAsync());
        menu.Items.Add(trayScan);
        menu.Items.Add("시스템 문제", null, (_, _) => OpenPage(1));
        menu.Items.Add("설정", null, (_, _) => OpenPage(8));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) =>
        {
            exitRequested = true;
            Close();
        });

        tray = new Forms.NotifyIcon
        {
            Icon = trayIcon,
            Text = "DK Easy Windows 시스템 점검",
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.MouseDoubleClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
                RestoreWindow();
        };

        if (settings.CheckOnLaunch)
            await RunScanAsync();
    }

    private void Navigation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (NavigationList.SelectedItem is not ListBoxItem selected || PageTitle is null)
            return;

        PageTitle.Text = (string)selected.Content;
        var page = (string)selected.Tag;
        DashboardPage.Visibility = page == "dashboard" ? Visibility.Visible : Visibility.Collapsed;
        EventsPage.Visibility = page == "events" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        PendingPage.Visibility = page is "dashboard" or "events" or "settings" ? Visibility.Collapsed : Visibility.Visible;
        PendingText.Text = page switch
        {
            "storage" => "대용량 파일·폴더 및 임시파일 조회는 준비 중입니다. 시스템 드라이브 용량은 대시보드에서 확인할 수 있습니다.",
            "startup" => "시작 프로그램 조회 및 관리는 준비 중입니다.",
            "services" => "서비스 조회 및 관리는 준비 중입니다.",
            "tasks" => "예약 작업 조회 및 관리는 준비 중입니다.",
            "apps" => "설치 프로그램 조회 및 제거 도구 연결은 준비 중입니다.",
            "history" => "점검 기록 저장과 이전 점검 비교는 준비 중입니다.",
            _ => ""
        };
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await RunScanAsync();

    private void CancelScan_Click(object sender, RoutedEventArgs e)
    {
        scanCancellation?.Cancel();
        CancelScanButton.IsEnabled = false;
        StatusText.Text = "검사 취소 중…";
    }

    private async Task RunScanAsync()
    {
        if (scanCancellation is not null)
            return;

        using var cancellation = new CancellationTokenSource();
        scanCancellation = cancellation;
        var token = cancellation.Token;
        var scanSettings = settings;
        var failures = 0;
        ScanButton.IsEnabled = false;
        if (trayScan is not null)
            trayScan.Enabled = false;
        ExportButton.IsEnabled = false;
        CancelScanButton.IsEnabled = true;
        CancelScanButton.Visibility = Visibility.Visible;
        ScanProgress.Value = 0;
        ScanProgress.Visibility = Visibility.Visible;
        StatusText.Text = "1/2 · 시스템 정보 및 드라이브 조회 중…";
        try
        {
            try
            {
                var overview = await Task.Run(MaintenanceScan.ReadOverview, token);
                token.ThrowIfCancellationRequested();
                ShowOverview(overview);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                failures++;
                ComputerText.Text = "시스템 정보 조회 실패";
                WindowsText.Text = error.Message;
                DriveHeading.Text = "시스템 드라이브";
                DriveSpace.Text = "용량 조회 실패";
                DriveUsage.Visibility = Visibility.Collapsed;
            }

            token.ThrowIfCancellationRequested();
            ScanProgress.Value = 1;
            StatusText.Text = "2/2 · 시스템·응용 프로그램 이벤트 로그 조회 중…";
            try
            {
                var snapshot = await Task.Run(
                    () => MaintenanceScan.ReadSystemEvents(scanSettings.EventDays, scanSettings.IncludeWarnings, token), token);
                token.ThrowIfCancellationRequested();
                events = snapshot;
                EventsGrid.ItemsSource = snapshot.Entries;
                EventSummary.Text = $"최근 {scanSettings.EventDays}일 · {snapshot.Entries.Count:N0}건"
                    + (snapshot.IncludesWarnings ? " · 경고 포함" : " · 심각·오류 및 문제 보고")
                    + (snapshot.IsTruncated ? " · 최신 200건만 표시" : "");
            }
            catch (Exception error) when (error is EventLogException or UnauthorizedAccessException)
            {
                failures++;
                events = null;
                EventsGrid.ItemsSource = null;
                EventSummary.Text = $"이벤트 로그 조회 실패 · {error.Message}";
            }

            ScanProgress.Value = 2;
            var result = failures switch { 0 => "검사 완료", 1 => "검사 완료 · 1개 단계 조회 실패", _ => "검사 실패 · 2개 단계 조회 실패" };
            LastScanText.Text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} · {result}";
            StatusText.Text = result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            StatusText.Text = "검사 취소됨 · 완료된 단계의 결과는 화면에서 확인할 수 있습니다.";
        }
        finally
        {
            scanCancellation = null;
            if (trayScan is not null)
                trayScan.Enabled = true;
            ScanButton.IsEnabled = true;
            ExportButton.IsEnabled = events is not null;
            CancelScanButton.Visibility = Visibility.Collapsed;
            ScanProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowOverview(SystemOverview overview)
    {
        ComputerText.Text = overview.Computer;
        WindowsText.Text = overview.Windows;
        DriveHeading.Text = $"시스템 드라이브 · {overview.Drive}";
        var used = overview.Total - overview.Free;
        DriveSpace.Text = $"사용 {FormatSize(used)}  /  총 {FormatSize(overview.Total)}  ·  남음 {FormatSize(overview.Free)}";
        DriveUsage.Value = overview.Total == 0 ? 0 : 100.0 * used / overview.Total;
        DriveUsage.Visibility = Visibility.Visible;
    }

    private static string FormatSize(long bytes) => $"{bytes / (1024.0 * 1024 * 1024):N1} GiB";

    private void Event_Changed(object sender, SelectionChangedEventArgs e)
    {
        var entry = EventsGrid.SelectedItem as SystemEvent;
        EventDetails.IsEnabled = entry is not null;
        if (entry is null)
            EventDetails.IsExpanded = false;
        EventMessage.Text = entry is null ? "" :
            $"{entry.Channel} · 레코드 ID {entry.RecordId?.ToString() ?? "없음"}{Environment.NewLine}{Environment.NewLine}"
            + (entry.Message ?? "이 이벤트의 설명을 Windows에서 읽을 수 없습니다.");
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (events is not { } snapshot)
            return;

        var dialog = new SaveFileDialog
        {
            FileName = $"windows-system-events-{DateTime.Now:yyyyMMdd-HHmmss}",
            Filter = "텍스트 보고서 (*.txt)|*.txt|JSON 데이터 (*.json)|*.json",
            DefaultExt = ".txt",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var report = Path.GetExtension(dialog.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase)
                ? JsonSerializer.Serialize(new { Channels = new[] { "System", "Application" }, snapshot.SinceUtc, snapshot.ThroughUtc,
                    snapshot.IncludesWarnings, snapshot.IsTruncated, snapshot.Entries }, new JsonSerializerOptions
                    { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All) })
                : FormatReport(snapshot);
            File.WriteAllText(dialog.FileName, report, Encoding.UTF8);
            StatusText.Text = $"시스템 이벤트 보고서 저장 · {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, error.Message, "보고서 저장 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string FormatReport(EventSnapshot snapshot)
    {
        var report = new StringBuilder();
        report.AppendLine("Windows 시스템 이벤트 보고서");
        report.AppendLine($"채널: System, Application / 범위(현지 시간): {snapshot.SinceUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz} ~ {snapshot.ThroughUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine($"수준: {(snapshot.IncludesWarnings ? "심각·오류·경고" : "심각·오류")} 및 Windows 오류 보고 / 결과: {snapshot.Entries.Count}건");
        if (snapshot.IsTruncated)
            report.AppendLine("최신 200건만 포함합니다.");

        foreach (var entry in snapshot.Entries)
        {
            report.AppendLine();
            report.AppendLine($"[{entry.DisplayTime}] {entry.Severity} / {entry.Channel} / {entry.Provider} / 이벤트 ID {entry.EventId} / 레코드 ID {entry.RecordId}");
            report.AppendLine(entry.Message ?? "이벤트 설명을 읽을 수 없습니다.");
        }
        return report.ToString();
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var updated = new MaintenanceSettings((CloseBehavior)CloseSetting.SelectedIndex,
            MinimizeSetting.IsChecked == true, LaunchSetting.IsChecked == true,
            Periods[DaysSetting.SelectedIndex], WarningSetting.IsChecked == true);
        try
        {
            updated.Save();
            settings = updated;
            if (scanCancellation is null)
                StatusText.Text = "설정 저장됨 · 이벤트 조회 옵션은 다음 검사부터 적용됩니다.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, error.Message, "설정 저장 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenPage(int page)
    {
        RestoreWindow();
        NavigationList.SelectedIndex = page;
    }

    private void RestoreWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && settings.MinimizeToTray && tray is not null)
            Hide();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (exitRequested || tray is null || settings.CloseBehavior == CloseBehavior.Exit)
            return;

        if (settings.CloseBehavior == CloseBehavior.Ask)
        {
            var choice = MessageBox.Show(this,
                "예: 트레이로 숨기기\n아니요: 프로그램 종료\n취소: 창 유지",
                "창 닫기", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (choice == MessageBoxResult.No)
                return;
            if (choice == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }
        }

        e.Cancel = true;
        Hide();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        scanCancellation?.Cancel();
        if (tray is not null)
        {
            tray.Visible = false;
            tray.ContextMenuStrip?.Dispose();
            tray.Dispose();
        }
        trayIcon?.Dispose();
    }
}
