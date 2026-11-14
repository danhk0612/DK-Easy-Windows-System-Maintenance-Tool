using System;
using System.IO;
using System.Text.Json;

namespace DK.Easy.WindowsMaintenance;

internal enum CloseBehavior { Ask, Tray, Exit }

internal sealed record MaintenanceSettings(
    CloseBehavior CloseBehavior = CloseBehavior.Ask,
    bool MinimizeToTray = false,
    bool CheckOnLaunch = false,
    int EventDays = 7,
    bool IncludeWarnings = false)
{
    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DKEasyWindowsMaintenance", "settings.json");

    internal static MaintenanceSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<MaintenanceSettings>(File.ReadAllText(SettingsFile));
            if (settings is null || !Enum.IsDefined(settings.CloseBehavior) || settings.EventDays is not (1 or 7 or 14 or 30))
                throw new JsonException("설정 값이 올바르지 않습니다.");
            return settings;
        }
        catch (FileNotFoundException)
        {
            return new MaintenanceSettings();
        }
        catch (DirectoryNotFoundException)
        {
            return new MaintenanceSettings();
        }
    }

    internal void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        var temporaryFile = SettingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryFile, SettingsFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryFile))
                File.Delete(temporaryFile);
        }
    }
}
