using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WaveEdit.Util;

/// <summary>
/// A persistent most-recently-used file list. Stored as newline-delimited absolute
/// paths (most recent first) under %AppData%\WaveEdit\recent.txt.
/// </summary>
public sealed class RecentFiles
{
    private const int MaxItems = 10;
    private readonly string _storePath;
    private readonly List<string> _items = new();

    public RecentFiles() : this(DefaultStorePath()) { }

    /// <summary>Construct with an explicit store path (used by tests).</summary>
    public RecentFiles(string storePath)
    {
        _storePath = storePath;
        Load();
    }

    private static string DefaultStorePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WaveEdit", "recent.txt");

    public IReadOnlyList<string> Items => _items;

    /// <summary>Move (or add) a path to the front of the list and persist.</summary>
    public void Add(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch { full = path; }

        _items.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        _items.Insert(0, full);
        if (_items.Count > MaxItems) _items.RemoveRange(MaxItems, _items.Count - MaxItems);
        Save();
    }

    public void Remove(string path)
    {
        _items.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    public void Clear()
    {
        _items.Clear();
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_storePath)) return;
            foreach (var line in File.ReadAllLines(_storePath))
            {
                var p = line.Trim();
                if (p.Length > 0 && _items.Count < MaxItems) _items.Add(p);
            }
        }
        catch { /* corrupt or unreadable store: start empty */ }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            File.WriteAllLines(_storePath, _items.Take(MaxItems));
        }
        catch { /* non-fatal: persistence is best-effort */ }
    }
}
