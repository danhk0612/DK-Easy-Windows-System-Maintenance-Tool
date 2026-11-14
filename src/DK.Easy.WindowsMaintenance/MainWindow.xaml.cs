using System;
using System.ComponentModel;
using System.Diagnostics.Eventing.Reader;
using System.Drawing;
using System.IO;
using System.Linq;
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
        InitializeInventory();
        InitializeAntivirusTree();
        RestoreScanResults();
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
        FileThreshold.Text = settings.FileThreshold.ToString();
        FolderThreshold.Text = settings.FolderThreshold.ToString();
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
        menu.Items.Add("시스템 문제", null, (_, _) => OpenPage("events"));
        menu.Items.Add("설정", null, (_, _) => OpenPage("settings"));
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
        if (sender is not ListBox list || list.SelectedItem is not ListBoxItem selected || PageTitle is null)
            return;

        var other = list == NavigationList ? UtilityNavigationList : NavigationList;
        if (other is not null)
            other.SelectedIndex = -1;
        PageTitle.Text = (string)selected.Content;
        var page = (string)selected.Tag;
        DashboardPage.Visibility = page == "dashboard" ? Visibility.Visible : Visibility.Collapsed;
        OverviewScanButton.Visibility = DashboardPage.Visibility;
        EventsPage.Visibility = page == "events" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        activePage = page;
        AntivirusPage.Visibility = page == "antivirus" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "antivirus") RefreshAntivirus();
        UnlockPage.Visibility = page == "unlock" ? Visibility.Visible : Visibility.Collapsed;
        InventoryPage.Visibility = page is "dashboard" or "events" or "settings" or "antivirus" or "unlock" ? Visibility.Collapsed : Visibility.Visible;
        RefreshInventory();
        RefreshScanTime();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await RunScanAsync();

    private void CancelScan_Click(object sender, RoutedEventArgs e)
    {
        scanCancellation?.Cancel();
        antivirusCancellation?.Cancel();
        CancelScanButton.IsEnabled = false;
        StatusText.Text = "검사 취소 중…";
    }

    private async Task RunScanAsync(string? only = null, bool afterManagement = false)
    {
        if (scanCancellation is not null || antivirusCancellation is not null || (managing && !afterManagement))
            return;

        using var cancellation = new CancellationTokenSource();
        scanCancellation = cancellation;
        var token = cancellation.Token;
        var scanSettings = settings;
        var failures = 0;
        string? cacheError = null;
        var stages = only is null ? new[] { "dashboard", "events", "storage", "startup", "services", "tasks", "apps" } : new[] { only };
        var scanDrive = (string?)DriveSelect.SelectedItem;
        ScanProgress.Maximum = stages.Length;
        RefreshInspectionButtons();
        DriveSelect.IsEnabled = false;
        ExportButton.IsEnabled = false;
        CancelScanButton.IsEnabled = true;
        CancelScanButton.Visibility = Visibility.Visible;
        ScanProgress.Value = 0;
        ScanProgress.Visibility = Visibility.Visible;
        try
        {
            var completedStages = 0;
            var current = new System.Collections.Generic.Dictionary<string, string[]>();
            SystemOverview? overview = null;
            int? eventCount = null;
            StorageResult? storage = null;
            foreach (var stage in stages)
            {
                token.ThrowIfCancellationRequested();
                StatusText.Text = $"{completedStages + 1}/{stages.Length} · {PageName(stage)} 조회 중…";
                try
                {
                    switch (stage)
                    {
                        case "dashboard":
                            overview = await Task.Run(MaintenanceScan.ReadOverview, token);
                            token.ThrowIfCancellationRequested();
                            ShowOverview(overview);
                            inventoryErrors.Remove(stage);
                            cacheError = await SaveScanResultAsync(stage, overview, DateTimeOffset.Now) ?? cacheError;
                            break;
                        case "events":
                            var snapshot = await Task.Run(
                                () => MaintenanceScan.ReadSystemEvents(scanSettings.EventDays, scanSettings.IncludeWarnings, token), token);
                            token.ThrowIfCancellationRequested();
                            ShowEvents(snapshot);
                            inventoryErrors.Remove(stage);
                            eventCount = snapshot.Entries.Count;
                            cacheError = await SaveScanResultAsync(stage, snapshot, DateTimeOffset.Now) ?? cacheError;
                            break;
                        case "storage":
                            if (scanDrive is null)
                                throw new IOException("조회 가능한 드라이브가 없습니다.");
                            var storageStage = completedStages;
                            ScanProgress.IsIndeterminate = true;
                            StatusText.Text = "저장 공간 · 최상위 항목 집계 중…";
                            var progress = new Progress<(int Done, int Total, string Path)>(value =>
                            {
                                if (scanCancellation != cancellation || token.IsCancellationRequested || completedStages != storageStage)
                                    return;
                                ScanProgress.IsIndeterminate = value.Total == 0;
                                var fraction = value.Total == 0 ? 0 : (double)value.Done / value.Total;
                                ScanProgress.Value = storageStage + fraction;
                                StatusText.Text = value.Total == 0 ? $"저장 공간 · {value.Path} 조회 중…" :
                                    $"저장 공간 · {fraction:P0} · 최상위 {value.Done:N0}/{value.Total:N0} · {value.Path}";
                            });
                            storage = await Task.Run(() => StorageScan.RunWithProgress(
                                scanDrive, scanSettings.FileThreshold, scanSettings.FolderThreshold, token, progress), token);
                            token.ThrowIfCancellationRequested();
                            storageResults[scanDrive] = storage;
                            cacheError = await SaveScanResultAsync(StorageCacheKey(scanDrive), storage, DateTimeOffset.Now) ?? cacheError;
                            inventoryErrors.Remove(stage);
                            break;
                        case "history":
                            history = await Task.Run(InspectionHistory.Read, token);
                            inventoryErrors.Remove(stage);
                            break;
                        default:
                            var rows = await Task.Run(() => CollectInventory(stage, token), token);
                            token.ThrowIfCancellationRequested();
                            inventoryResults[stage] = rows;
                            cacheError = await SaveScanResultAsync(stage, rows, DateTimeOffset.Now) ?? cacheError;
                            current[stage] = rows.Select(row => row.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                            inventoryErrors.Remove(stage);
                            break;
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or EventLogException
                    or System.Security.SecurityException or Win32Exception or JsonException)
                {
                    failures++;
                    inventoryErrors[stage] = error.Message;
                    if (stage == "events" && events is null)
                        EventSummary.Text = $"이벤트 로그 조회 실패 · {error.Message}";
                }
                RefreshOverviewCounts();
                RefreshScanTime();
                ScanProgress.IsIndeterminate = false;
                ScanProgress.Value = ++completedStages;
                if (stage == activePage)
                    RefreshInventory();
            }

            token.ThrowIfCancellationRequested();
            string? historyError = null;
            if (only is null && failures < stages.Length)
            {
                try
                {
                    var entry = new InventorySnapshot(DateTimeOffset.Now, overview?.Free,
                        storage?.Large.Count, storage?.TempCount, eventCount, current);
                    history = await Task.Run(() => InspectionHistory.Add(entry));
                    inventoryErrors.Remove("history");
                    if (activePage == "history")
                        RefreshInventory();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
                {
                    historyError = error.Message;
                }
            }

            var result = failures == 0 ? "검사 완료" : $"검사 종료 · {failures}/{stages.Length}개 조회 실패";
            if (only is null)
                LastScanText.Text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} · {result}";
            var saveError = historyError ?? cacheError;
            StatusText.Text = saveError is null ? result : $"{result} · 기록 저장 실패: {saveError}";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            StatusText.Text = "검사 취소됨 · 완료된 단계의 결과는 화면에서 확인할 수 있습니다.";
        }
        finally
        {
            scanCancellation = null;
            RefreshInspectionButtons();
            DriveSelect.IsEnabled = true;
            ExportButton.IsEnabled = events is not null;
            CancelScanButton.Visibility = Visibility.Collapsed;
            ScanProgress.IsIndeterminate = false;
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

    private static string FormatSize(long bytes) => StorageEntry.FormatSize(bytes);

    private void Event_Changed(object sender, SelectionChangedEventArgs e)
    {
        var entry = EventsGrid.SelectedItem as SystemEvent;
        EventDetails.IsEnabled = entry is not null;
        if (entry is null)
            EventDetails.IsExpanded = false;
        EventMessage.Text = entry is null ? "" :
            $"{entry.Channel} · 레코드 ID {entry.RecordId?.ToString() ?? "없음"}{Environment.NewLine}{Environment.NewLine}"
            + DescribeEvent(entry);
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
            report.AppendLine(DescribeEvent(entry));
            if (entry.Xml is not null) report.AppendLine(entry.Xml);
        }
        return report.ToString();
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(FileThreshold.Text, out var fileGiB) || fileGiB is < 1 or > 1024
            || !int.TryParse(FolderThreshold.Text, out var folderGiB) || folderGiB is < 1 or > 1024)
        {
            MessageBox.Show(this, "크기 기준은 1~1024 사이의 정수(GiB)를 입력하세요.", "설정", MessageBoxButton.OK);
            return;
        }

        var updated = new MaintenanceSettings((CloseBehavior)CloseSetting.SelectedIndex,
            MinimizeSetting.IsChecked == true, LaunchSetting.IsChecked == true,
            Periods[DaysSetting.SelectedIndex], WarningSetting.IsChecked == true, fileGiB, folderGiB);
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

    private void OpenPage(string page)
    {
        RestoreWindow();
        var list = page is "history" or "settings" ? UtilityNavigationList : NavigationList;
        list.SelectedItem = list.Items.OfType<ListBoxItem>().Single(item => Equals(item.Tag, page));
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
        antivirusCancellation?.Cancel();
        if (tray is not null)
        {
            tray.Visible = false;
            tray.ContextMenuStrip?.Dispose();
            tray.Dispose();
        }
        unlockCancellation?.Cancel();
        trayIcon?.Dispose();
    }
}
