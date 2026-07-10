using System;
using System.IO;
using System.Text.Json;
using DreamOfElectricStorage.Core;

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

    /// <summary>"Clusters" (relationship-well map, default) or "Cells" (circle-pack hierarchy).</summary>
    public string ViewMode { get; set; } = "Clusters";

    // Clusters-view per-force strengths (0..1). Defaults mirror ForceWeights: type low so
    // the map doesn't blob out of the box; relationships pull hardest.
    public double ForceSizeGravity { get; set; } = 0.80;
    public double ForceDuplicate { get; set; } = 0.90;
    public double ForceSimilarName { get; set; } = 0.75;
    public double ForceFolder { get; set; } = 0.50;
    public double ForceDate { get; set; } = 0.30;
    public double ForceType { get; set; } = 0.20;

    public ForceWeights ToForceWeights() => new(
        ForceSizeGravity, ForceDuplicate, ForceSimilarName, ForceFolder, ForceDate, ForceType);

    public bool ShowLegend { get; set; } = true;

    public bool ReduceMotion { get; set; }

    /// <summary>Show NTFS system/metadata roots ($Recycle.Bin, System Volume Information).</summary>
    public bool ShowSystemFolders { get; set; }

    // Window placement — restored on launch. Bounds are the *restored* (un-maximized)
    // rectangle; WindowMaximized is applied on top. Zero W/H = "never saved, use default".
    public bool WindowMaximized { get; set; }
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public int WindowX { get; set; }
    public int WindowY { get; set; }

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
