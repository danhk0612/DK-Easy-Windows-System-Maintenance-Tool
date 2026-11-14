using System;
using System.Diagnostics;
using System.IO;
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
}
