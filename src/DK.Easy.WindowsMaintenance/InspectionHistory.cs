using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DK.Easy.WindowsMaintenance;

internal sealed record InventorySnapshot(DateTimeOffset When, long? SystemFreeBytes,
    int? LargeItems, int? TemporaryItems, int? Events, Dictionary<string, string[]> Groups)
{
    public string DisplayTime => When.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public int RecordedGroups => Groups.Count;
}

internal static class InspectionHistory
{
    private static readonly string HistoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DKEasyWindowsMaintenance", "history.json");

    internal static List<InventorySnapshot> Read()
    {
        if (!File.Exists(HistoryPath))
            return [];
        return JsonSerializer.Deserialize<List<InventorySnapshot>>(File.ReadAllText(HistoryPath))
            ?? throw new JsonException("점검 기록을 읽을 수 없습니다.");
    }

    internal static List<InventorySnapshot> Add(InventorySnapshot entry)
    {
        var history = Read();
        history.Insert(0, entry);
        var newest = history.Take(12).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
        var temp = HistoryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(newest, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, HistoryPath, overwrite: true);
            return newest;
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
