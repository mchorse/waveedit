using System;
using System.Collections.Generic;
using WaveEdit.Audio;

namespace WaveEdit.Edit;

/// <summary>
/// Executes edit commands and maintains undo/redo history for a single document.
/// Commands carry their own inverse data, so history cost scales with edit size,
/// not with the whole file.
/// </summary>
public sealed class UndoStack
{
    private readonly Stack<IEditCommand> _undo = new();
    private readonly Stack<IEditCommand> _redo = new();

    public event Action? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoName => _undo.Count > 0 ? _undo.Peek().Name : null;
    public string? RedoName => _redo.Count > 0 ? _redo.Peek().Name : null;

    /// <summary>Run a command and push it on the undo stack.</summary>
    public void Execute(IEditCommand cmd, AudioDocument doc)
    {
        cmd.Do(doc);
        _undo.Push(cmd);
        _redo.Clear();
        Changed?.Invoke();
    }

    public void Undo(AudioDocument doc)
    {
        if (_undo.Count == 0) return;
        var cmd = _undo.Pop();
        cmd.Undo(doc);
        _redo.Push(cmd);
        Changed?.Invoke();
    }

    public void Redo(AudioDocument doc)
    {
        if (_redo.Count == 0) return;
        var cmd = _redo.Pop();
        cmd.Do(doc);
        _undo.Push(cmd);
        Changed?.Invoke();
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }
}
