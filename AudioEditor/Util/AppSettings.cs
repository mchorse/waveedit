using System;
using System.Collections.Generic;
using System.IO;

namespace WaveEdit.Util;

/// <summary>
/// Tiny persistent key/value settings store (one `key=value` per line, UTF-8 no BOM),
/// kept in %AppData%\WaveEdit\settings.cfg. Best-effort: failures are swallowed.
/// </summary>
public static class AppSettings
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WaveEdit", "settings.cfg");

    private static readonly Dictionary<string, string> Map = Load();

    public static string? Get(string key) => Map.TryGetValue(key, out var v) ? v : null;

    public static void Set(string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) Map.Remove(key);
        else Map[key] = value;
        Save();
    }

    /// <summary>Endpoint ID of the most recently chosen recording input, or null.</summary>
    public static string? LastInputDeviceId
    {
        get => Get("LastInputDeviceId");
        set => Set("LastInputDeviceId", value);
    }

    private static Dictionary<string, string> Load()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(StorePath))
                foreach (var line in File.ReadAllLines(StorePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    map[line[..eq].Trim()] = line[(eq + 1)..];
                }
        }
        catch { /* start empty */ }
        return map;
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var lines = new List<string>(Map.Count);
            foreach (var kv in Map) lines.Add($"{kv.Key}={kv.Value}");
            File.WriteAllLines(StorePath, lines, new System.Text.UTF8Encoding(false));
        }
        catch { /* best-effort */ }
    }
}
