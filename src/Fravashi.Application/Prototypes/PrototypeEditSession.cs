namespace Fravashi.Application.Prototypes;

/// <summary>
/// One prototype open for direct edits on its page (backlog 214, plan @39), shaped like
/// <see cref="Mockups.MockupEditSession"/> so the two editors behave alike: the document as it is now, an undo and a
/// redo stack, and whether it differs from what was opened.
/// <para>
/// <b>Every panel edit appends exactly ONE overlay operation.</b> The renderer replays the overlay in order and the last
/// <c>setText</c>, or the last <c>setCss</c> of a property, wins, so appending is correct and an earlier operation is
/// never rewritten: one change a person made is one operation a reviewer sees.
/// </para>
/// <para>
/// Backlog 216 (plan @41): settling an operation that no longer places is the two other changes, each on ONE existing
/// operation at its index: <see cref="Reanchor"/> replaces it in place with the same operation on another anchor (in
/// place, because its position in a last-wins overlay is part of what it means) and <see cref="Drop"/> removes it.
/// Every change goes on one history: undo reverses the last exactly (an operation dropped goes back at its index), redo
/// does it again, and a new change clears redo. Undo never reaches past what this session changed.
/// </para>
/// <para>
/// <see cref="IsDirty"/> compares <see cref="PrototypeWriter"/>'s canonical bytes with those of the document that was
/// opened (or last saved), so undoing every edit is clean again.
/// </para>
/// </summary>
public sealed class PrototypeEditSession
{
    private readonly List<PrototypeOperation> overlay;
    private readonly Stack<Change> undo = new();
    private readonly Stack<Change> redo = new();
    private readonly PrototypeDocument opened;
    private string clean;

    /// <summary>One change at <paramref name="Index"/>: an append has no <paramref name="Before"/>, a drop no <paramref name="After"/>.</summary>
    private sealed record Change(int Index, PrototypeOperation? Before, PrototypeOperation? After);

    private PrototypeEditSession(PrototypeDocument document)
    {
        opened = document;
        overlay = [.. document.Overlay];
        clean = PrototypeWriter.Write(document);
    }

    /// <summary>Opens a document, which is valid by construction.</summary>
    public static PrototypeEditSession Open(PrototypeDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new PrototypeEditSession(document);
    }

    /// <summary>The document as it is now: what was opened, with every change not undone.</summary>
    public PrototypeDocument Document => opened with { Overlay = overlay.ToArray() };

    /// <summary>The overlay as it is now, in the order it applies.</summary>
    public IReadOnlyList<PrototypeOperation> Overlay => overlay;

    /// <summary>True when the document is not the one that was opened or last saved.</summary>
    public bool IsDirty => !string.Equals(PrototypeWriter.Write(Document), clean, StringComparison.Ordinal);

    /// <summary>Whether there is a change made in this session to undo.</summary>
    public bool CanUndo => undo.Count > 0;

    /// <summary>Whether there is an undone change to redo.</summary>
    public bool CanRedo => redo.Count > 0;

    /// <summary>Appends one operation; the redo stack is cleared, as after any new edit.</summary>
    public void Apply(PrototypeOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Do(new Change(overlay.Count, null, operation));
    }

    /// <summary>
    /// Moves the operation at <paramref name="index"/> to <paramref name="anchor"/>, in place (backlog 216). False, and
    /// nothing changed, when there is no such operation, it is a page rule (which has no anchor), or it is already there.
    /// </summary>
    public bool Reanchor(int index, PrototypeAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        if (index < 0 || index >= overlay.Count || WithAnchor(overlay[index], anchor) is not { } moved || moved == overlay[index])
        {
            return false;
        }

        Do(new Change(index, overlay[index], moved));
        return true;
    }

    /// <summary>Removes the operation at <paramref name="index"/> (backlog 216). False when there is none.</summary>
    public bool Drop(int index)
    {
        if (index < 0 || index >= overlay.Count)
        {
            return false;
        }

        Do(new Change(index, overlay[index], null));
        return true;
    }

    /// <summary>
    /// Reverses the last change made in this session. False when there is none: what the document held when it was
    /// opened is not Undo's to take back.
    /// </summary>
    public bool Undo()
    {
        if (!undo.TryPop(out var change))
        {
            return false;
        }

        Reverse(change);
        redo.Push(change);
        return true;
    }

    /// <summary>Makes the last undone change again. False when there is none.</summary>
    public bool Redo()
    {
        if (!redo.TryPop(out var change))
        {
            return false;
        }

        Make(change);
        undo.Push(change);
        return true;
    }

    /// <summary>Makes the document as it is now the clean point (it has just been saved). The history stays.</summary>
    public void MarkSaved() => clean = PrototypeWriter.Write(Document);

    /// <summary>The same operation on another anchor, or null for a page rule, which has none.</summary>
    public static PrototypeOperation? WithAnchor(PrototypeOperation operation, PrototypeAnchor anchor) => operation switch
    {
        PrototypeSetTextOperation text => text with { Anchor = anchor },
        PrototypeSetCssOperation css => css with { Anchor = anchor },
        PrototypeSetAttributeOperation attribute => attribute with { Anchor = anchor },
        PrototypeAddClassOperation addClass => addClass with { Anchor = anchor },
        PrototypeInsertHtmlOperation insert => insert with { Anchor = anchor },
        _ => null,
    };

    private void Do(Change change)
    {
        Make(change);
        undo.Push(change);
        redo.Clear();
    }

    private void Make(Change change)
    {
        switch (change)
        {
            case { Before: null, After: { } after }:
                overlay.Insert(change.Index, after);
                break;
            case { After: null }:
                overlay.RemoveAt(change.Index);
                break;
            default:
                overlay[change.Index] = change.After!;
                break;
        }
    }

    private void Reverse(Change change)
    {
        switch (change)
        {
            case { Before: null }:
                overlay.RemoveAt(change.Index);
                break;
            case { After: null, Before: { } before }:
                overlay.Insert(change.Index, before);
                break;
            default:
                overlay[change.Index] = change.Before!;
                break;
        }
    }
}
