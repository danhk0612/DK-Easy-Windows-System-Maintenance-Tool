using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace DK.Easy.WindowsMaintenance;

public partial class MainWindow
{
    private CancellationTokenSource? unlockCancellation;
    private string? searchedUnlockPath;

    private void RefreshUnlockButtons()
    {
        if (UnlockCloseApp is null)
            return;
        var available = !InspectionBusy;
        UnlockPath.IsEnabled = available;
        UnlockBrowseFile.IsEnabled = available;
        UnlockBrowseFolder.IsEnabled = available;
        UnlockSearch.IsEnabled = available && !string.IsNullOrWhiteSpace(UnlockPath.Text);
        UnlockCloseApp.IsEnabled = available && searchedUnlockPath is not null
            && UnlockHolders.SelectedItem is FileLocks.Holder { CanClose: true };
        UnlockCancel.Visibility = unlockCancellation is null ? Visibility.Collapsed : Visibility.Visible;
        UnlockCancel.IsEnabled = unlockCancellation is { IsCancellationRequested: false };
    }

    private async void OpenUnlockPath(string path)
    {
        if (InspectionBusy)
            return;
        OpenPage("unlock");
        UnlockPath.Text = path;
        await SearchUnlockAsync();
    }

    private void Unlock_Click(object sender, RoutedEventArgs e)
    {
        if (InventoryGrid.SelectedItem is StorageEntry file)
            OpenUnlockPath(file.Path);
    }

    private void UnlockBrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "잠금을 확인할 파일" };
        if (dialog.ShowDialog(this) == true)
            UnlockPath.Text = dialog.FileName;
    }

    private void UnlockBrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "잠금을 확인할 폴더" };
        if (dialog.ShowDialog(this) == true)
            UnlockPath.Text = dialog.FolderName;
    }

    private void UnlockPath_Changed(object sender, TextChangedEventArgs e)
    {
        searchedUnlockPath = null;
        if (UnlockHolders is not null)
        {
            UnlockHolders.ItemsSource = null;
            UnlockStatus.Text = "폴더를 선택하면 하위 파일도 확인합니다.";
        }
        RefreshUnlockButtons();
    }

    private async void UnlockPath_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        await SearchUnlockAsync();
    }

    private async void UnlockSearch_Click(object sender, RoutedEventArgs e) => await SearchUnlockAsync();
    private void UnlockHolders_Changed(object sender, SelectionChangedEventArgs e) => RefreshUnlockButtons();

    private void UnlockCancel_Click(object sender, RoutedEventArgs e)
    {
        unlockCancellation?.Cancel();
        RefreshUnlockButtons();
    }

    private async void UnlockCloseApp_Click(object sender, RoutedEventArgs e)
    {
        if (InspectionBusy || searchedUnlockPath is null || UnlockHolders.SelectedItem is not FileLocks.Holder selected)
            return;
        if (MessageBox.Show(this, $"{selected.Label}\n\n작업을 저장했다면 이 앱에 닫기 요청을 보낼까요?",
            "앱 닫기 요청", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
            await SearchUnlockAsync(selected);
    }

    private async Task SearchUnlockAsync(FileLocks.Holder? close = null)
    {
        if (InspectionBusy || string.IsNullOrWhiteSpace(UnlockPath.Text))
            return;
        var path = close is null ? UnlockPath.Text.Trim().Trim('"') : searchedUnlockPath!;
        using var cancellation = new CancellationTokenSource();
        unlockCancellation = cancellation;
        managing = true;
        searchedUnlockPath = null;
        UnlockHolders.ItemsSource = null;
        UnlockStatus.Text = close is null ? "점유 앱 검색 중…" : "점유 앱을 다시 확인하는 중…";
        RefreshInspectionButtons();
        try
        {
            if (!Path.IsPathFullyQualified(path))
                throw new ArgumentException("드라이브부터 시작하는 전체 경로를 입력하세요.");
            var token = cancellation.Token;
            var result = await Task.Run(() => FileLocks.Search(path, token), token);
            token.ThrowIfCancellationRequested();
            var prefix = "";
            if (close is not null)
            {
                var current = result.Holders.FirstOrDefault(holder => holder.Pid == close.Pid
                    && holder.StartedAt == close.StartedAt && holder.CanClose);
                if (current is not null && FileLocks.RequestClose(current))
                {
                    prefix = "앱 닫기 요청을 보냈습니다. ";
                    await Task.Delay(1200, token);
                    result = await Task.Run(() => FileLocks.Search(path, token), token);
                }
                else
                    prefix = "닫을 앱을 다시 확인해 주세요. ";
            }
            token.ThrowIfCancellationRequested();
            searchedUnlockPath = path;
            UnlockHolders.ItemsSource = result.Holders;
            UnlockStatus.Text = prefix + (result.LockedFiles == 0
                ? "확인한 범위에서 잠긴 파일을 찾지 못했습니다."
                : $"잠긴 파일 {result.LockedFiles:N0}개 · 확인된 점유 앱 {result.Holders.Length:N0}개")
                + (result.Skipped == 0 ? "" : $" · 건너뛴 항목 {result.Skipped:N0}개");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            UnlockStatus.Text = "검색을 취소했습니다.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or NotSupportedException)
        {
            UnlockStatus.Text = $"점유 앱 검색 실패 · {error.Message}";
        }
        finally
        {
            unlockCancellation = null;
            managing = false;
            RefreshInspectionButtons();
        }
    }
}
