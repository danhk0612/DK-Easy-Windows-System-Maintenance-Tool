using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;


namespace DK.Easy.WindowsMaintenance;

public partial class MainWindow
{
    private string activePage = "dashboard";
    private readonly Dictionary<string, StorageResult> storageResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<InventoryEntry>> inventoryResults = new();
    private readonly Dictionary<string, string> inventoryErrors = new();
    private readonly HashSet<string> filters = new();
    private List<InventorySnapshot> history = [];
    private bool refreshingInventory;
    private bool managing;

    private void InitializeInventory()
    {
        DriveSelect.ItemsSource = DriveInfo.GetDrives()
            .Where(drive => drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.IsReady)
            .Select(drive => drive.Name).ToArray();
        RefreshOverviewCounts();
        DriveSelect.SelectedItem = Path.GetPathRoot(Environment.SystemDirectory);
        if (DriveSelect.SelectedIndex < 0 && DriveSelect.Items.Count > 0)
            DriveSelect.SelectedIndex = 0;
        try
        {
            history = InspectionHistory.Read();
            if (history.FirstOrDefault() is { } last)
                LastScanText.Text = $"{last.DisplayTime} · 저장된 전체 점검";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            inventoryErrors["history"] = error.Message;
        }
    }

    private void RefreshOverviewCounts()
    {
        var counts = new List<string>();
        foreach (var page in new[] { "startup", "services", "tasks", "apps" })
        {
            var value = inventoryResults.TryGetValue(page, out var list) ? $"{list.Count:N0}개" :
                inventoryErrors.ContainsKey(page) ? "조회 실패" : "미검사";
            counts.Add($"{PageName(page)} {value}");
        }
        OverviewCounts.Text = string.Join("  ·  ", counts);
    }

    private static string PageName(string page) => page switch
    {
        "dashboard" => "시스템 정보",
        "events" => "시스템 문제",
        "storage" => "저장 공간",
        "startup" => "시작 프로그램",
        "services" => "서비스",
        "tasks" => "예약 작업",
        "apps" => "설치 프로그램",
        _ => "점검 기록"
    };

    private static async Task<IReadOnlyList<InventoryEntry>> CollectInventory(string page, CancellationToken token) =>
        page switch
        {
            "startup" => InventoryScan.Startup(token),
            "services" => await InventoryScan.Services(token),
            "tasks" => await InventoryScan.Tasks(token),
            "apps" => InventoryScan.InstalledApps(token),
            _ => throw new ArgumentOutOfRangeException(nameof(page))
        };

    private void RefreshInventory()
    {
        if (InventoryGrid is null || activePage is "dashboard" or "events" or "settings" or "antivirus" or "unlock")
            return;

        refreshingInventory = true;
        try
        {
            var page = activePage;
            InventoryGrid.ItemsSource = null;
            InventoryGrid.Columns.Clear();
            InventoryDetails.IsExpanded = false;
            DriveSelect.Visibility = page == "storage" ? Visibility.Visible : Visibility.Collapsed;
            InspectPageButton.Visibility = Visibility.Visible;
            FilterSwitch.Visibility = page is "storage" or "services" or "tasks" ? Visibility.Visible : Visibility.Collapsed;
            FilterSwitch.Content = page == "storage" ? "임시파일 보기" : "Windows 항목 포함";
            FilterSwitch.IsChecked = filters.Contains(page);
            OpenLocationButton.Visibility = page is "storage" or "startup" or "apps" ? Visibility.Visible : Visibility.Collapsed;
            UnlockButton.Visibility = page == "storage" ? Visibility.Visible : Visibility.Collapsed;
            ManageButton.Visibility = page == "history" ? Visibility.Collapsed : Visibility.Visible;
            ManageButton.Content = page == "apps" ? "선택 항목 관리" :
                page == "storage" ? "휴지통으로 이동" : "선택 항목 관리";
            InventorySummary.Text = inventoryErrors.TryGetValue(page, out var error)
                ? $"조회 실패 · {error}" : "미검사";

            if (page == "storage")
            {
                AddColumn("경로", "Path", new DataGridLength(1, DataGridLengthUnitType.Star));
                AddColumn("종류", "Kind", 84);
                AddColumn("크기", "Size", 95, "Bytes");
                AddColumn("수정 시각", "Modified", 143, format: "yyyy-MM-dd HH:mm");
                if (DriveSelect.SelectedItem is string drive && storageResults.TryGetValue(drive, out var storage))
                {
                    InventoryGrid.ItemsSource = filters.Contains(page) ? storage.Temporary : storage.Large;
                    InventorySummary.Text = filters.Contains(page)
                        ? $"사용자 임시파일 {storage.TempCount:N0}개 · {FormatSize(storage.TempBytes)} · 큰 순서로 최대 300개 표시"
                        : $"{drive} · 큰 파일·폴더 {storage.Large.Count:N0}개 표시 · 최대 300개";
                    if (storage.Skipped > 0)
                        InventorySummary.Text += $" · 읽을 수 없거나 링크인 항목 {storage.Skipped:N0}개 건너뜀";
                }
            }
            else if (page == "history")
            {
                AddColumn("점검 시각", "DisplayTime", new DataGridLength(1, DataGridLengthUnitType.Star));
                AddColumn("확인된 목록 수", "RecordedGroups", 130);
                AddColumn("시스템 문제", "Events", 105);
                InventoryGrid.ItemsSource = history;
                if (error is null)
                    InventorySummary.Text = history.Count == 0 ? "저장된 전체 점검이 없습니다." : $"최근 전체 점검 {history.Count}회";
            }
            else
            {
                AddColumn("이름", "Name", new DataGridLength(1, DataGridLengthUnitType.Star));
                AddColumn("상태 / 버전", "State", 145);
                AddColumn("게시자 / 원본", "Publisher", 155);
                if (inventoryResults.TryGetValue(page, out var rows))
                {
                    var shown = filters.Contains(page) ? rows : rows.Where(row => !row.SystemItem).ToArray();
                    InventoryGrid.ItemsSource = shown;
                    InventorySummary.Text = $"{shown.Count:N0}개 표시 · 총 {rows.Count:N0}개";
                }
            }
        }
        finally
        {
            refreshingInventory = false;
            UpdateInventorySelection();
        }
    }

    private void AddColumn(string title, string binding, DataGridLength width, string? sort = null, string? format = null)
    {
        InventoryGrid.Columns.Add(new DataGridTextColumn
        {
            Header = title, Width = width, MinWidth = 70, SortMemberPath = sort ?? binding,
            Binding = new Binding(binding) { StringFormat = format }
        });
    }

    private void Drive_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (activePage == "storage")
        {
            RefreshInventory();
            RefreshScanTime();
        }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (refreshingInventory || FilterSwitch is null)
            return;
        if (FilterSwitch.IsChecked == true)
            filters.Add(activePage);
        else
            filters.Remove(activePage);
        RefreshInventory();
    }

    private void InventorySelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!refreshingInventory)
            UpdateInventorySelection();
    }

    private void UpdateInventorySelection()
    {
        if (InventoryDetails is null)
            return;
        var selection = InventoryGrid.SelectedItem;
        InventoryDetails.IsEnabled = selection is not null;
        UnlockButton.IsEnabled = !InspectionBusy && selection is StorageEntry;
        OpenLocationButton.IsEnabled = selection is StorageEntry || selection is InventoryEntry { Target: not null };
        ManageButton.IsEnabled = !InspectionBusy && (activePage == "apps"
            || selection is StorageEntry || selection is InventoryEntry);
        if (selection is null)
            InventoryDetails.IsExpanded = false;
        InventoryDetailText.Text = selection switch
        {
            StorageEntry file => $"{file.Path}\n{file.Size} · {file.Modified:yyyy-MM-dd HH:mm:ss}"
                + (MaintenanceActions.CanRecycle(file) ? "" : "\n휴지통 이동 제한: " + MaintenanceActions.RecycleRestriction(file)),
            InventoryEntry item => $"{item.Name}\n{item.Location}\n{item.State}\n"
                + $"게시자 / 원본: {item.Publisher ?? "확인 불가"}\n"
                + (activePage == "apps" ? $"설치일: {item.InstallDate ?? "확인 불가"} · 크기: "
                    + (item.EstimatedKiB is long size ? $"{FormatSize(size * 1024)}\n" : "확인 불가\n") : "")
                + (item.Detail ?? "") + (MaintenanceActions.ManagementRestriction(activePage, item) is { } reason ? $"\n\n관리 제한: {reason}" : ""),
            InventorySnapshot item => DescribeHistory(item),
            _ => ""
        };
    }

    private string DescribeHistory(InventorySnapshot item)
    {
        var lines = new List<string>
        {
            item.DisplayTime,
            $"시스템 드라이브 남은 용량: {(item.SystemFreeBytes is long free ? FormatSize(free) : "조회 실패")}",
            $"큰 항목 표시 수: {item.LargeItems?.ToString() ?? "조회 실패"} / 임시파일: {item.TemporaryItems?.ToString() ?? "조회 실패"}",
            $"시스템 문제: {item.Events?.ToString() ?? "조회 실패"}"
        };
        var previous = history.SkipWhile(entry => entry != item).Skip(1).FirstOrDefault();
        if (item.SystemFreeBytes is long now && previous?.SystemFreeBytes is long before && now != before)
            lines.Add($"직전 대비 남은 용량 {FormatSize(Math.Abs(now - before))} {(now > before ? "증가" : "감소")}");
        foreach (var (group, ids) in item.Groups)
        {
            var line = $"{PageName(group)}: {ids.Length}개";
            if (previous?.Groups.TryGetValue(group, out var older) == true)
                line += $" · 추가 {ids.Except(older, StringComparer.OrdinalIgnoreCase).Count()} / 제거 {older.Except(ids, StringComparer.OrdinalIgnoreCase).Count()}";
            lines.Add(line);
        }
        return string.Join(Environment.NewLine, lines);
    }

    private async void InspectPage_Click(object sender, RoutedEventArgs e) => await RunScanAsync(activePage);

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        var target = InventoryGrid.SelectedItem switch
        {
            StorageEntry item => item.Path,
            InventoryEntry item => item.Target,
            _ => null
        };
        if (target is null || !Path.IsPathFullyQualified(target))
            return;

        try
        {
            var explorer = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
                { UseShellExecute = false };
            if (Directory.Exists(target))
                explorer.ArgumentList.Add(target);
            else if (File.Exists(target))
                explorer.ArgumentList.Add($"/select,{target}");
            else
                throw new IOException("선택한 위치를 찾을 수 없습니다.");
            using var process = Process.Start(explorer);
        }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException)
        {
            MessageBox.Show(this, error.Message, "위치 열기 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Manage_Click(object sender, RoutedEventArgs e)
    {
        if (InspectionBusy)
            return;

        var page = activePage;
        var selected = InventoryGrid.SelectedItem;
        try
        {
            if (page == "apps")
            {
                if (selected is InventoryEntry app && MessageBox.Show(this,
                    $"{app.Name}\n\nWindows의 설치된 앱 화면을 열까요? 앱 이름으로 검색할 수 있습니다.",
                    "설치 프로그램 관리", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;
                OpenWindowsManager(page);
                return;
            }

            if (selected is StorageEntry file)
            {
                if (MaintenanceActions.RecycleRestriction(file) is { } restriction)
                {
                    InventoryDetails.IsExpanded = true;
                    MessageBox.Show(this, restriction, "휴지통 이동 제한", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (MessageBox.Show(this, $"{file.Path}\n{file.Size}\n\n선택한 파일을 휴지통으로 이동할까요?",
                        "파일 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                    return;
                MaintenanceActions.Recycle(file);
                var drive = (string)DriveSelect.SelectedItem;
                var old = storageResults[drive];
                storageResults[drive] = old with
                {
                    Large = old.Large.Where(item => !item.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase)).ToArray(),
                    Temporary = old.Temporary.Where(item => !item.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase)).ToArray(),
                    TempCount = file.Kind == "임시파일" ? old.TempCount - 1 : old.TempCount,
                    TempBytes = file.Kind == "임시파일" ? old.TempBytes - file.Bytes : old.TempBytes
                };
                managing = true;
                RefreshInspectionButtons();
                var cacheError = scanTimes.TryGetValue(StorageCacheKey(drive), out var previousScan)
                    ? await SaveScanResultAsync(StorageCacheKey(drive), storageResults[drive], previousScan) : null;
                RefreshInventory();
                StatusText.Text = "휴지통으로 이동했습니다. 폴더 합계를 갱신하려면 다시 검사하세요.";
                if (cacheError is not null)
                    StatusText.Text += $" · 기록 저장 실패: {cacheError}";
                return;
            }

            if (selected is not InventoryEntry row)
                return;
            if (MaintenanceActions.ManagementRestriction(page, row) is { } reason)
            {
                InventoryDetails.IsExpanded = true;
                if (MessageBox.Show(this, $"{reason}\n\nWindows 기본 관리 화면을 열까요?",
                    "관리 제한", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    OpenWindowsManager(page);
                return;
            }

            var command = ChooseManagementAction(page, row);
            if (command is null)
                return;
            if (MessageBox.Show(this, $"{row.Name}\n{row.Location}\n\n{command.Value.Label}\n{command.Value.Impact}",
                    "변경 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            managing = true;
            RefreshInspectionButtons();
            StatusText.Text = $"{command.Value.Label} 요청 처리 중…";
            await MaintenanceActions.Apply(page, row, command.Value.Action);
            await RunScanAsync(page, afterManagement: true);
            StatusText.Text = $"변경 요청 처리 완료 · {StatusText.Text}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception
            or System.Security.SecurityException or OperationCanceledException)
        {
            StatusText.Text = "작업 실패 또는 취소";
            MessageBox.Show(this, error.Message, "작업 결과", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            managing = false;
            RefreshInspectionButtons();
        }
    }

    private ManagementCommand? ChooseManagementAction(string page, InventoryEntry row)
    {
        var actions = MaintenanceActions.Commands(page, row);
        if (actions.Length == 1)
            return actions[0];

        var panel = new StackPanel { Margin = new Thickness(18) };
        var dialog = new Window
        {
            Title = row.Name, Owner = this, ShowInTaskbar = false, Width = 300,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, Content = panel
        };
        ManagementCommand? selected = null;
        foreach (var action in actions)
        {
            var button = new Button { Content = action.Label, Margin = new Thickness(0, 0, 0, 6) };
            button.Click += (_, _) => { selected = action; dialog.DialogResult = true; };
            panel.Children.Add(button);
        }
        var cancel = new Button { Content = "취소", IsCancel = true };
        panel.Children.Add(cancel);
        dialog.ShowDialog();
        return selected;
    }
}
