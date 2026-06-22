using System;
using WaveEdit.Audio;

namespace WaveEdit.Edit;

/// <summary>A reversible edit applied to an <see cref="AudioDocument"/>.</summary>
public interface IEditCommand
{
    string Name { get; }
    void Do(AudioDocument doc);
    void Undo(AudioDocument doc);
}

/// <summary>Delete (cut) a frame range. Stores the removed audio so it can be restored.</summary>
public sealed class DeleteRangeCommand : IEditCommand
{
    private readonly long _start;
    private long _length;
    private float[][]? _removed;

    public string Name => "Delete";

    public DeleteRangeCommand(long start, long length)
    {
        _start = start;
        _length = length;
    }

    public void Do(AudioDocument doc)
    {
        long start = Math.Clamp(_start, 0, doc.Length);
        _length = Math.Clamp(_length, 0, doc.Length - start);
        _removed = doc.ExtractRange(start, _length);

        long newLen = doc.Length - _length;
        var nc = new float[doc.ChannelCount][];
        for (int c = 0; c < doc.ChannelCount; c++)
        {
            var dst = new float[newLen];
            Array.Copy(doc.Channels[c], 0, dst, 0, start);
            Array.Copy(doc.Channels[c], start + _length, dst, start, newLen - start);
            nc[c] = dst;
        }
        doc.SetChannels(nc);
    }

    public void Undo(AudioDocument doc)
    {
        if (_removed == null) return;
        EditOps.InsertAt(doc, _start, _removed);
    }
}

/// <summary>Insert silence (or pasted/recorded audio) at a frame position.</summary>
public sealed class InsertCommand : IEditCommand
{
    private readonly long _pos;
    private readonly long _frames;     // used when _data == null (silence)
    private readonly float[][]? _data; // explicit audio to insert
    private long _insertedLength;

    public string Name { get; }

    /// <summary>Insert silence of <paramref name="frames"/> length.</summary>
    public InsertCommand(long pos, long frames, string name = "Insert silence")
    {
        _pos = pos;
        _frames = frames;
        Name = name;
    }

    /// <summary>Insert explicit audio (paste / recorded clip).</summary>
    public InsertCommand(long pos, float[][] data, string name = "Insert")
    {
        _pos = pos;
        _data = data;
        Name = name;
    }

    public void Do(AudioDocument doc)
    {
        if (_data != null)
        {
            _insertedLength = _data[0].LongLength;
            EditOps.InsertAt(doc, _pos, _data);
        }
        else
        {
            _insertedLength = _frames;
            EditOps.InsertSilence(doc, _pos, _frames);
        }
    }

    public void Undo(AudioDocument doc)
    {
        var del = new DeleteRangeCommand(_pos, _insertedLength);
        del.Do(doc);
    }
}

/// <summary>
/// Apply an in-place processing function to a range (amplify, fade, normalize, silence).
/// Captures the pre-edit block for undo; redo re-runs the function deterministically.
/// </summary>
public sealed class ProcessRangeCommand : IEditCommand
{
    private readonly long _start;
    private readonly long _length;
    private readonly Action<float[][], long, long> _process;
    private float[][]? _before;

    public string Name { get; }

    public ProcessRangeCommand(string name, long start, long length, Action<float[][], long, long> process)
    {
        Name = name;
        _start = start;
        _length = length;
        _process = process;
    }

    public void Do(AudioDocument doc)
    {
        _before = doc.ExtractRange(_start, _length);
        _process(doc.Channels, _start, _length);
        doc.Modified = true;
    }

    public void Undo(AudioDocument doc)
    {
        if (_before == null) return;
        for (int c = 0; c < doc.ChannelCount; c++)
            Array.Copy(_before[c], 0, doc.Channels[c], _start, _before[c].LongLength);
        doc.Modified = true;
    }
}

/// <summary>Low-level splice helpers shared by commands.</summary>
internal static class EditOps
{
    public static void InsertAt(AudioDocument doc, long pos, float[][] data)
    {
        pos = Math.Clamp(pos, 0, doc.Length);
        long add = data[0].LongLength;
        long newLen = doc.Length + add;
        var nc = new float[doc.ChannelCount][];
        for (int c = 0; c < doc.ChannelCount; c++)
        {
            var dst = new float[newLen];
            Array.Copy(doc.Channels[c], 0, dst, 0, pos);
            // data may have fewer channels than the doc; wrap around if so.
            var src = data[Math.Min(c, data.Length - 1)];
            Array.Copy(src, 0, dst, pos, add);
            Array.Copy(doc.Channels[c], pos, dst, pos + add, doc.Length - pos);
            nc[c] = dst;
        }
        doc.SetChannels(nc);
    }

    public static void InsertSilence(AudioDocument doc, long pos, long frames)
    {
        pos = Math.Clamp(pos, 0, doc.Length);
        long newLen = doc.Length + frames;
        var nc = new float[doc.ChannelCount][];
        for (int c = 0; c < doc.ChannelCount; c++)
        {
            var dst = new float[newLen];
            Array.Copy(doc.Channels[c], 0, dst, 0, pos);
            // gap stays zero-filled
            Array.Copy(doc.Channels[c], pos, dst, pos + frames, doc.Length - pos);
            nc[c] = dst;
        }
        doc.SetChannels(nc);
    }
}
