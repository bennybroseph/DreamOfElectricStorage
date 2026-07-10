using System;
using System.IO;
using System.Text.Json;

namespace DreamOfElectricStorage.App;

/// <summary>
/// App settings persisted to LocalAppData (same pattern as PinStore).
/// Load/save failures are swallowed — defaults always work.
/// </summary>
public sealed class SettingsStore
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DreamOfElectricStorage", "settings.json");

    /// <summary>"System", "Light", or "Dark".</summary>
    public string Theme { get; set; } = "System";

    public bool ShowLegend { get; set; } = true;

    public bool ReduceMotion { get; set; }

    public static SettingsStore Load()
    {
        try
        {
            if (File.Exists(StorePath)
                && JsonSerializer.Deserialize<SettingsStore>(File.ReadAllText(StorePath)) is { } settings)
                return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new SettingsStore();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
