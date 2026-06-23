using System.Collections.Generic;

namespace WaveEdit.Audio;

/// <summary>
/// Process-wide cache of the recording input list so the Record dialog can show
/// instantly after the first (slow) WASAPI scan. The cache is refreshed in the
/// background on each open to pick up plugged/unplugged devices.
/// </summary>
public static class DeviceCache
{
    private static readonly object Sync = new();
    private static List<InputDevice>? _devices;
    private static string? _defaultId;

    /// <summary>Cached snapshot, or null if nothing has been enumerated yet.</summary>
    public static (List<InputDevice> Devices, string? DefaultId)? Snapshot()
    {
        lock (Sync) return _devices == null ? null : (_devices, _defaultId);
    }

    /// <summary>Enumerate devices and update the cache. Safe to call from any thread.</summary>
    public static (List<InputDevice> Devices, string? DefaultId) Refresh()
    {
        var devices = AudioRecorder.EnumerateDevices();
        var def = AudioRecorder.DefaultInputDeviceId();
        lock (Sync) { _devices = devices; _defaultId = def; }
        return (devices, def);
    }

    /// <summary>Warm the cache (e.g. at startup); failures are ignored.</summary>
    public static void Prime()
    {
        try { Refresh(); } catch { /* the dialog will retry on open */ }
    }

    /// <summary>True if two device lists differ in count or endpoint IDs (order included).</summary>
    public static bool Differ(IReadOnlyList<InputDevice> a, IReadOnlyList<InputDevice> b)
    {
        if (a.Count != b.Count) return true;
        for (int i = 0; i < a.Count; i++)
            if (!string.Equals(a[i].Id, b[i].Id, System.StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
