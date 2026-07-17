using System;
using System.Collections.Generic;
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

    // Clusters-view grouping: an ordered, root-first list of the ENABLED facets (a nested GROUP BY).
    // Disabled facets are simply absent. Default = relationship-led broad-to-narrow nesting.
    public string[] FacetOrder { get; set; } = ["Type", "Folder", "SimilarName", "Duplicate"];

    // Always-on layout coefficients (0..1-ish). Anchor holds nodes at their packed slots (the pack
    // arranges heavier-central); cohesion/repulsion are global.
    public double Anchor { get; set; } = 0.80;
    public double Cohesion { get; set; } = 1.00;
    public double Repulsion { get; set; } = 1.00;

    public LayoutTuning ToTuning() => new(Anchor, Repulsion, Cohesion);

    /// <summary>Parse the persisted facet-name list into a <see cref="Core.FacetOrder"/>,
    /// dropping unknown/duplicate entries.</summary>
    public FacetOrder ToFacetOrder()
    {
        var kinds = new List<WellKind>();
        foreach (string s in FacetOrder)
            if (Enum.TryParse(s, out WellKind k) && !kinds.Contains(k))
                kinds.Add(k);
        return new Core.FacetOrder(kinds);
    }

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
