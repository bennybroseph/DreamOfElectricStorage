using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DreamOfElectricStorage.App;

/// <summary>
/// Pinned places, persisted as a JSON path list in LocalAppData. Paths (not FRNs) —
/// they survive re-indexing and reboots; resolution happens via VolumeIndex.FindByPath
/// at navigation time. Load/save failures are swallowed (pins are a convenience).
/// </summary>
public sealed class PinStore
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DreamOfElectricStorage", "pins.json");

    private readonly List<string> _pins = Load();

    public IReadOnlyList<string> Pins => _pins;

    public bool Contains(string path) =>
        _pins.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Pin ⇄ unpin. Returns true when the path is pinned afterwards.</summary>
    public bool Toggle(string path)
    {
        int existing = _pins.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        bool pinned;
        if (existing >= 0)
        {
            _pins.RemoveAt(existing);
            pinned = false;
        }
        else
        {
            _pins.Add(path);
            pinned = true;
        }
        Save();
        return pinned;
    }

    private static List<string> Load()
    {
        try
        {
            if (File.Exists(StorePath)
                && JsonSerializer.Deserialize<List<string>>(File.ReadAllText(StorePath)) is { } pins)
                return pins;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return [];
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_pins));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
