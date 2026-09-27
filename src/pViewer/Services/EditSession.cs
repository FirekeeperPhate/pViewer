using System.Windows.Media.Imaging;
using pViewer.Imaging;

namespace pViewer.Services;

/// <summary>
/// Image being edited, with undo/redo history. States are frozen BitmapSources
/// (immutable), so each step is just a reference. Memory is capped by a budget.
/// </summary>
public sealed class EditSession
{
    private const long UndoBudgetBytes = 1_200_000_000;
    private const int MaxSteps = 30;

    private readonly LinkedList<BitmapSource> _undo = new();
    private readonly Stack<BitmapSource> _redo = new();

    public EditSession(BitmapSource original)
    {
        Saved = original;
        Current = original;
    }

    /// <summary>Last saved state (initially, the loaded image).</summary>
    public BitmapSource Saved { get; private set; }
    public BitmapSource Current { get; private set; }

    /// <summary>True if there are unsaved changes.</summary>
    public bool IsModified => !ReferenceEquals(Current, Saved);

    public void MarkSaved() => Saved = Current;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Apply(BitmapSource result)
    {
        if (ReferenceEquals(result, Current)) return;
        _undo.AddLast(Current);
        _redo.Clear();
        Current = result;
        TrimHistory();
    }

    public bool Undo()
    {
        if (_undo.Last is not { } last) return false;
        _undo.RemoveLast();
        _redo.Push(Current);
        Current = last.Value;
        return true;
    }

    public bool Redo()
    {
        if (!_redo.TryPop(out var next)) return false;
        _undo.AddLast(Current);
        Current = next;
        return true;
    }

    private void TrimHistory()
    {
        long total = _undo.Sum(ImageBridge.EstimateBytes) + ImageBridge.EstimateBytes(Current);
        while (_undo.Count > 0 && (_undo.Count > MaxSteps || total > UndoBudgetBytes))
        {
            total -= ImageBridge.EstimateBytes(_undo.First!.Value);
            _undo.RemoveFirst();
        }
    }
}
