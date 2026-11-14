using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace DK.Easy.WindowsMaintenance;

public partial class MainWindow
{
    private static string DescribeEvent(SystemEvent entry)
    {
        var payload = entry.Payload();
        return $"시각: {entry.DisplayTime}\n수준: {entry.Severity}\n채널: {entry.Channel}\n"
            + $"원본: {entry.Provider ?? "없음"} · 이벤트 ID: {entry.EventId} · 레코드 ID: {entry.RecordId}\n"
            + $"컴퓨터: {entry.Computer ?? "없음"} · 프로세스 ID: {entry.ProcessId?.ToString() ?? "없음"}"
            + $" · 스레드 ID: {entry.ThreadId?.ToString() ?? "없음"}\n\n"
            + (entry.Message ?? "Windows 이벤트 설명을 읽을 수 없습니다.")
            + (payload.Length == 0 ? "" : "\n\n이벤트 데이터\n" + payload);
    }

    private void Row_RightClick(object sender, MouseButtonEventArgs e)
    {
        var grid = (DataGrid)sender;
        var row = e.OriginalSource is DependencyObject source
            ? ItemsControl.ContainerFromElement(grid, source) as DataGridRow
            : null;
        grid.SelectedItem = row?.Item;
    }

    private void Row_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        var grid = (DataGrid)sender;
        if (e.ChangedButton == MouseButton.Left && e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(grid, source) is DataGridRow row)
        {
            grid.SelectedItem = row.Item;
            ShowRowDetails(grid);
        }
    }

    private void Row_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var grid = (DataGrid)sender;
        if (grid.SelectedItem is null)
        {
            e.Handled = true;
            return;
        }
        var menu = grid.ContextMenu;
        menu.Items.Clear();

        void Add(string title, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = title, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Add("상세 정보", () => ShowRowDetails(grid));
        if (grid.SelectedItem is SystemEvent entry)
        {
            Add("이벤트 XML 복사", () => CopyDetail(entry.Xml), entry.Xml is not null);
            Add("이벤트 뷰어 열기", () => OpenWindowsManager("events"));
            return;
        }
        if (grid.SelectedItem is StorageEntry storage)
        {
            Add("경로 복사", () => CopyDetail(storage.Path));
            if (storage.Kind != "폴더")
                Add("파일 잠금 확인 / 해제", () => UnlockFileAsync(storage.Path), !InspectionBusy);
            Add("이 위치 바이러스 검사", () => SelectAntivirusTarget(storage.Path), !InspectionBusy);
        }
        if (OpenLocationButton.Visibility == Visibility.Visible)
            Add("위치 열기", () => OpenLocation_Click(grid, e), OpenLocationButton.IsEnabled);
        if (ManageButton.Visibility == Visibility.Visible)
            Add(ManageButton.Content.ToString()!, () => Manage_Click(grid, e), ManageButton.IsEnabled);
    }

    private void ShowRowDetails(DataGrid grid)
    {
        var tabs = new TabControl { Margin = new Thickness(12) };
        void AddTab(string title, string value) => tabs.Items.Add(new TabItem
        {
            Header = title,
            Content = new TextBox { Text = value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8) }
        });

        if (grid.SelectedItem is SystemEvent entry)
        {
            AddTab("이벤트 정보", DescribeEvent(entry));
            AddTab("원본 XML", entry.Xml ?? "Windows에서 원본 XML을 읽을 수 없습니다.");
        }
        else if (grid.SelectedItem is not null)
            AddTab("항목 정보", InventoryDetailText.Text);
        else
            return;

        new Window { Owner = this, Title = $"{PageTitle.Text} · 상세 정보", Width = 720, Height = 510,
            MinWidth = 420, MinHeight = 250, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = tabs }.ShowDialog();
    }

    private void CopyDetail(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;
        try { Clipboard.SetText(value); }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(this, "클립보드에 복사할 수 없습니다.", "복사", MessageBoxButton.OK);
        }
    }

    private void OpenWindowsManager(string page)
    {
        var system = Environment.SystemDirectory;
        var target = page switch
        {
            "startup" => "ms-settings:startupapps",
            "apps" => "ms-settings:appsfeatures",
            "services" => Path.Combine(system, "services.msc"),
            "tasks" => Path.Combine(system, "taskschd.msc"),
            _ => Path.Combine(system, "eventvwr.msc")
        };
        try { using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception error)
        {
            MessageBox.Show(this, error.Message, "Windows 관리 화면 열기 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Unlock_Click(object sender, RoutedEventArgs e)
    {
        if (InventoryGrid.SelectedItem is StorageEntry { Kind: not "폴더" } file)
            UnlockFileAsync(file.Path);
    }

    private async void UnlockFileAsync(string path)
    {
        if (InspectionBusy)
            return;
        managing = true;
        RefreshInspectionButtons();
        try
        {
            for (string? parent = path; parent is not null; parent = Path.GetDirectoryName(parent))
                if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("연결 경로의 파일은 잠금 해제를 할 수 없습니다.");
            if (await Task.Run(() => FileLocks.IsAvailable(path)))
            {
                MessageBox.Show(this, "현재 잠겨 있지 않습니다.", "파일 잠금", MessageBoxButton.OK);
                return;
            }

            var holders = await Task.Run(() => FileLocks.Find(path));
            var list = new ListBox { ItemsSource = holders, DisplayMemberPath = nameof(FileLocks.Holder.Label), MinHeight = 95 };
            var close = new Button { Content = "선택한 앱 닫기 요청", IsEnabled = false, Margin = new Thickness(0, 8, 0, 0) };
            var cancel = new Button { Content = "닫기", IsCancel = true, Margin = new Thickness(0, 6, 0, 0) };
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock { Text = path, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = holders.Length == 0
                ? "Windows에서 이 파일을 사용 중인 앱을 찾지 못했습니다."
                : "작업을 저장한 뒤 닫을 앱을 선택하세요. 앱에서 저장 확인이 뜰 수 있습니다.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
            panel.Children.Add(list);
            panel.Children.Add(close);
            panel.Children.Add(cancel);
            var dialog = new Window { Owner = this, Title = "파일 잠금 확인 / 해제", Content = panel, Width = 465,
                SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            list.SelectionChanged += (_, _) => close.IsEnabled = list.SelectedItem is FileLocks.Holder { CanClose: true };
            close.Click += (_, _) => dialog.DialogResult = true;
            if (dialog.ShowDialog() != true || list.SelectedItem is not FileLocks.Holder selected)
                return;

            var released = await Task.Run(() => FileLocks.RequestCloseAsync(path, selected));
            MessageBox.Show(this, released ? "파일 잠금이 해제됐습니다."
                : "파일이 아직 잠겨 있습니다. 앱의 저장 확인을 마치거나 다른 앱을 닫은 뒤 다시 확인하세요.",
                "파일 잠금", MessageBoxButton.OK);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, error.Message, "파일 잠금 확인 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            managing = false;
            RefreshInspectionButtons();
        }
    }
}
