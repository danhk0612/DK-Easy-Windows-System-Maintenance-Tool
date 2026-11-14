using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;

namespace DK.Easy.WindowsMaintenance;

internal readonly record struct ManagementCommand(string Action, string Label, string Impact);

internal static class MaintenanceActions
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string DisabledRunKey = @"Software\DKEasyWindowsMaintenance\DisabledRun";

    internal static bool CanRecycle(StorageEntry file) => RecycleRestriction(file) is null;

    internal static string? RecycleRestriction(StorageEntry file)
    {
        if (file.Kind == "폴더")
            return "폴더 전체 이동은 지원하지 않습니다. 위치를 열어 개별 파일을 확인하세요.";

        var root = file.Kind == "임시파일" ? StorageScan.UserTemp :
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!InventoryScan.IsWithin(file.Path, root))
            return "현재 사용자 프로필 또는 사용자 임시파일 범위의 파일만 이동할 수 있습니다.";
        if (file.Kind != "임시파일" && (InventoryScan.IsWithin(file.Path, Path.Combine(root, "AppData"))
            || InventoryScan.IsWithin(file.Path, local) || InventoryScan.IsWithin(file.Path, roaming)))
            return "앱 데이터 경로는 보호됩니다.";
        return null;
    }

    internal static void Recycle(StorageEntry selected)
    {
        if (!CanRecycle(selected))
            throw new IOException("관리할 수 없는 경로입니다.");

        var file = new FileInfo(selected.Path);

        for (FileSystemInfo? part = file; part is not null;
             part = part is FileInfo item ? item.Directory : ((DirectoryInfo)part).Parent)
        {
            if ((part.Attributes & FileAttributes.ReparsePoint) != 0 || (part is FileInfo && (part.Attributes & FileAttributes.System) != 0))
                throw new IOException("시스템 파일 또는 연결 경로는 이동할 수 없습니다.");
        }
        if (file.Length != selected.Bytes || file.LastWriteTimeUtc != selected.ModifiedUtc)
            throw new IOException("검사 후 파일이 변경되었습니다. 다시 검사하세요.");

        using (file.Open(FileMode.Open, FileAccess.Read, FileShare.None)) { }
        FileSystem.DeleteFile(file.FullName, UIOption.AllDialogs, RecycleOption.SendToRecycleBin,
            UICancelOption.ThrowException);
        if (File.Exists(file.FullName))
            throw new IOException("원래 위치에 파일이 남아 있습니다.");
    }

    internal static bool Available(string page, InventoryEntry item) => page switch
    {
        "startup" => item.Location is "CurrentUser · Registry64 · Run" or "CurrentUser · Registry64 · 비활성",
        "services" => !item.SystemItem && item.Target is not null && File.Exists(item.Target)
            && !string.IsNullOrWhiteSpace(item.Publisher) && !item.Publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase),
        "tasks" => item.TaskDefinitionHash is not null && !item.SystemItem && !item.Location.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(item.Publisher) && !item.Publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    internal static string? ManagementRestriction(string page, InventoryEntry item)
    {
        if (page is not ("startup" or "services" or "tasks") || Available(page, item))
            return null;
        return page switch
        {
            "startup" when item.Location.EndsWith("RunOnce", StringComparison.Ordinal) => "RunOnce 항목은 조회 전용입니다.",
            "startup" => "앱에서 원본을 보존하며 변경할 수 있는 항목은 현재 사용자 64비트 Run입니다.",
            "services" => "Windows 서비스이거나 실행 파일과 타사 게시자를 확인할 수 없어 앱에서 변경할 수 없습니다.",
            _ => "Windows 작업이거나 작성자 또는 작업 정의를 확인할 수 없어 앱에서 변경할 수 없습니다."
        };
    }

    internal static ManagementCommand[] Commands(string page, InventoryEntry item) => page switch
    {
        "startup" when item.Location.EndsWith("비활성", StringComparison.Ordinal) =>
            [new("restore", "자동 실행 복원", "다음 로그인부터 이 프로그램이 다시 실행될 수 있습니다.")],
        "startup" => [new("disable", "자동 실행 비활성화", "원본 등록값을 보관합니다. 현재 실행 중인 앱은 계속 실행됩니다.")],
        "tasks" when item.State == "Disabled" =>
            [new("enable", "예약 작업 활성화", "다음 트리거부터 작업이 실행될 수 있습니다.")],
        "tasks" => [new("disable", "예약 작업 비활성화", "다음 트리거의 실행을 막습니다. 이미 실행 중인 작업은 계속 실행될 수 있습니다.")],
        "services" =>
        [
            new("start", "서비스 시작", "서비스 프로세스를 시작합니다."),
            new("stop", "서비스 중지", "이 서비스를 사용하는 앱에 영향이 있을 수 있습니다."),
            new("auto", "시작 유형 · 자동", "Windows 시작 시 서비스가 실행될 수 있습니다."),
            new("manual", "시작 유형 · 수동", "앱 또는 관리자가 서비스를 시작할 수 있습니다."),
            new("disabled", "시작 유형 · 사용 안 함", "다음부터 이 서비스가 시작되지 않습니다.")
        ],
        _ => []
    };

    internal static Task Apply(string page, InventoryEntry item, string action)
    {
        if (!Available(page, item))
            throw new IOException("이 항목은 변경할 수 없습니다.");
        return page == "startup" ? Task.Run(() => ChangeStartup(item, action)) : ChangeElevated(page, item, action);
    }

    private static void ChangeStartup(InventoryEntry item, string action)
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var run = hive.CreateSubKey(RunKey);
        using var backup = hive.CreateSubKey(DisabledRunKey);
        var restore = action == "restore";
        var from = restore ? backup : run;
        var to = restore ? run : backup;
        if (from.GetValue(item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string value
            || value != item.Detail || to.GetValue(item.Name) is not null)
            throw new IOException("검사 후 등록값이 변경되었거나 같은 이름의 값이 이미 있습니다. 다시 검사하세요.");

        var type = from.GetValueKind(item.Name);
        if (type is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
            throw new IOException("문자열 자동 실행 등록값만 변경할 수 있습니다.");
        to.SetValue(item.Name, value, type);
        try { from.DeleteValue(item.Name, throwOnMissingValue: true); }
        catch
        {
            to.DeleteValue(item.Name, throwOnMissingValue: false);
            throw;
        }
    }

    private static async Task ChangeElevated(string page, InventoryEntry item, string action)
    {
        var quotedName = Quote(item.Id);
        string body;
        if (page == "services")
        {
            var operation = action switch
            {
                "start" => "$controller | Start-Service",
                "stop" => "if (@($controller.DependentServices | Where-Object Status -ne Stopped).Count -ne 0) { throw 'Dependent service is active' }; $controller | Stop-Service -Confirm:$false",
                "auto" => "$controller | Set-Service -StartupType Automatic",
                "manual" => "$controller | Set-Service -StartupType Manual",
                "disabled" => "$controller | Set-Service -StartupType Disabled",
                _ => throw new ArgumentOutOfRangeException(nameof(action))
            };
            body = $$"""
                $name = {{quotedName}}
                $service = @(Get-CimInstance Win32_Service | Where-Object Name -CEQ $name)
                if ($service.Count -ne 1 -or $service[0].PathName -cne {{Quote(item.Detail!)}}) { throw 'Service changed' }
                $path = {{Quote(item.Target!)}}
                $windows = [Environment]::GetFolderPath('Windows').TrimEnd('\') + '\'
                if ([IO.Path]::GetFullPath($path).StartsWith($windows,[StringComparison]::OrdinalIgnoreCase)) { throw 'Windows service' }
                $signature = Get-AuthenticodeSignature -LiteralPath $path
                if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -match 'Microsoft') { throw 'Unsupported service signature' }
                $controller = @(Get-Service | Where-Object Name -CEQ $name)
                if ($controller.Count -ne 1) { throw 'Service changed' }
                {{operation}}
                """;
        }
        else
        {
            var operation = action == "enable" ? "Enable-ScheduledTask" : "Disable-ScheduledTask";
            body = $$"""
                $id = {{quotedName}}
                $task = @(Get-ScheduledTask | Where-Object { ($_.TaskPath + $_.TaskName) -ceq $id })
                if ($task.Count -ne 1 -or $task[0].TaskPath.StartsWith('\Microsoft\',[StringComparison]::OrdinalIgnoreCase)
                    -or $task[0].Author -cne {{Quote(item.Publisher!)}} -or $task[0].Author -match 'Microsoft') { throw 'Task changed' }
                $xml = Export-ScheduledTask -TaskName $task[0].TaskName -TaskPath $task[0].TaskPath
                $sha = [Security.Cryptography.SHA256]::Create()
                $hash = [Convert]::ToBase64String($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($xml)))
                if ($hash -cne {{Quote(item.TaskDefinitionHash!)}}) { throw 'Task definition changed' }
                $task[0] | {{operation}} | Out-Null
                """;
        }

        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        var script = "$ErrorActionPreference='Stop'; try {" + Environment.NewLine + body
            + Environment.NewLine + "} catch { exit 1 }; exit 0";
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(start) ?? throw new IOException("관리 작업을 시작하지 못했습니다.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new IOException("Windows가 변경을 완료하지 못했습니다. 대상이 바뀌었거나 권한·서명·종속성 조건을 확인해야 합니다.");
    }

    private static string Quote(string text) =>
        $"([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}')))";
}