using DreamOfElectricStorage.Core;

namespace DreamOfElectricStorage.App;

/// <summary>One row in the settings "Grouping" list: a facet, its label, and whether it's enabled.
/// List order (enabled rows, top-first) is the nesting order fed to the Clusters layout.</summary>
public sealed class FacetRow
{
    public WellKind Kind { get; init; }
    public string Label { get; init; } = "";
    public bool Enabled { get; set; }
}
