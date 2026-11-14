using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

if (!string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.Ordinal))
    throw new InvalidOperationException("Run this integration check in an isolated GitHub Actions runner.");

var app = Assembly.LoadFrom(Path.GetFullPath(args.Single()));
var root = Directory.CreateDirectory(Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    "dk-maintenance-" + Guid.NewGuid().ToString("N")));
var token = CancellationToken.None;
try
{
    var overview = Invoke("MaintenanceScan", "ReadOverview");
    Require((long)Property(overview, "Total") > 0, "System drive total");
    Require((long)Property(overview, "Free") >= 0, "System drive free");
    var path = Path.Combine(root.FullName, "fixture.txt");
    File.WriteAllText(path, "fixture");
    var storage = Invoke("StorageScan", "Run", root.FullName, 0, 1, token);
    Require(Rows(Property(storage, "Large")).Any(row => (string)Property(row, "Path") == path), "File inventory");
    Reject<OperationCanceledException>(() => Invoke("StorageScan", "Run", root.FullName, 0, 1, new CancellationToken(true)));

    var updates = new List<(int Done, int Total, string Path)>();
    Invoke("StorageScan", "RunWithProgress", root.FullName, 0, 1, token,
        new InlineProgress<(int Done, int Total, string Path)>(updates.Add));
    Require(updates.Count >= 2 && updates.First().Done == 0 && updates.Last().Done == updates.Last().Total
        && updates.Last().Total > 0, "Storage progress reaches its final count");

    var selected = Rows(Property(storage, "Large")).Single(row => (string)Property(row, "Path") == path);
    Require((bool)Invoke("MaintenanceActions", "CanRecycle", selected), "Fixture qualifies for recycle");
    using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        Reject<IOException>(() => Invoke("MaintenanceActions", "Recycle", selected));
    File.AppendAllText(path, "changed");
    Reject<IOException>(() => Invoke("MaintenanceActions", "Recycle", selected));
    Require(File.Exists(path), "Locked and changed fixtures stayed in place");

    var events = Invoke("MaintenanceScan", "ReadSystemEvents", 7, true, token);
    Require(Rows(Property(events, "Entries")).Count() <= 200, "Event inventory limit");
    foreach (var row in Rows(Property(events, "Entries")))
        Require((string)Property(row, "Channel") is "System" or "Application", "Event channel");

    foreach (var method in new[] { "Startup", "InstalledApps", "Services", "Tasks" })
    {
        var result = Invoke("InventoryScan", method, token);
        if (result is Task pending)
        {
            await pending;
            result = Property(pending, "Result");
        }
        Require(result is IEnumerable, $"{method} returns an inventory");
        if (method is "Services" or "Tasks")
            Require(Rows(result).Any(), $"{method} returns live rows");
        Console.WriteLine($"{method} inventory passed.");
    }

    const string runPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string backupPath = @"Software\DKEasyWindowsMaintenance\DisabledRun";
    var name = "DKEasyMaintenanceTest-" + Guid.NewGuid().ToString("N");
    var command = $@"""{Path.Combine(Environment.SystemDirectory, "cmd.exe")}"" /c exit 0";
    using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
    using var run = hive.CreateSubKey(runPath);
    try
    {
        run.SetValue(name, command, RegistryValueKind.ExpandString);
        await (Task)Invoke("MaintenanceActions", "Apply", "startup", FindStartup(name, "CurrentUser · Registry64 · Run"), "disable");
        Require(run.GetValue(name) is null, "Startup fixture disabled");
        using var backup = hive.OpenSubKey(backupPath, writable: true)!;
        Require(backup.GetValueKind(name) == RegistryValueKind.ExpandString, "Original registry type saved");
        await (Task)Invoke("MaintenanceActions", "Apply", "startup", FindStartup(name, "CurrentUser · Registry64 · 비활성"), "restore");
        Require((string?)run.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) == command
            && run.GetValueKind(name) == RegistryValueKind.ExpandString && backup.GetValue(name) is null,
            "Startup command and registry type restored");
        await (Task)Invoke("MaintenanceActions", "Apply", "startup", FindStartup(name, "CurrentUser · Registry64 · Run"), "delete");
        Require(run.GetValue(name) is null && backup.GetValue(name) is null, "Active startup registration removed");

        run.SetValue(name, command, RegistryValueKind.ExpandString);
        await (Task)Invoke("MaintenanceActions", "Apply", "startup", FindStartup(name, "CurrentUser · Registry64 · Run"), "disable");
        await (Task)Invoke("MaintenanceActions", "Apply", "startup", FindStartup(name, "CurrentUser · Registry64 · 비활성"), "delete");
        Require(run.GetValue(name) is null && backup.GetValue(name) is null, "Disabled startup registration removed");
        Console.WriteLine("Startup disable, restore and deletion passed.");
    }
    finally
    {
        run.DeleteValue(name, false);
        using var backup = hive.OpenSubKey(backupPath, writable: true);
        backup?.DeleteValue(name, false);
    }

    var snapshotType = app.GetType("DK.Easy.WindowsMaintenance.InventorySnapshot", true)!;
    var start = DateTimeOffset.UtcNow;
    for (var i = 0; i < 13; i++)
    {
        var snapshot = Activator.CreateInstance(snapshotType, start.AddSeconds(i), 100L, 1, 2, 3,
            new Dictionary<string, string[]> { ["startup"] = ["fixture"] })!;
        Invoke("InspectionHistory", "Add", snapshot);
    }
    var history = Rows(Invoke("InspectionHistory", "Read")).ToArray();
    Require(history.Length == 12 && (DateTimeOffset)Property(history[0], "When") == start.AddSeconds(12),
        "History persists newest twelve snapshots");
    Console.WriteLine("Overview, storage safety, event inventory and history passed.");
}
finally
{
    File.Delete(Path.Combine(root.FullName, "fixture.txt"));
    root.Delete();
}

object FindStartup(string name, string location) => Rows(Invoke("InventoryScan", "Startup", token))
    .Single(row => (string)Property(row, "Name") == name && (string)Property(row, "Location") == location);

object Invoke(string type, string name, params object[] arguments)
{
    var method = app.GetType("DK.Easy.WindowsMaintenance." + type, true)!
        .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    try
    {
        return method.Invoke(null, arguments)!;
    }
    catch (TargetInvocationException error) when (error.InnerException is not null)
    {
        ExceptionDispatchInfo.Capture(error.InnerException).Throw();
        throw;
    }
}

static object Property(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target)!;
static IEnumerable<object> Rows(object value) => ((IEnumerable)value).Cast<object>();

static void Require(bool condition, string check)
{
    if (!condition)
        throw new InvalidOperationException(check);
}

static void Reject<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
