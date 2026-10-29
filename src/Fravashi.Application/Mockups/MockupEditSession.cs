using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fravashi.Application.Mockups;

/// <summary>What became of one edit: applied, or refused with the sentences saying why.</summary>
public sealed record MockupEditOutcome(bool Applied, IReadOnlyList<MockupError> Refusal)
{
    internal static MockupEditOutcome Done { get; } = new(true, []);
}

/// <summary>
/// One mockup open for editing (#195): the tree as it is now, what the reader says about it, an undo and a
/// redo stack, and whether it differs from the tree that was opened. Every editing surface of #51 drives one
/// of these and none of them edits a tree any other way.
/// <para>
/// <b>The tree under edit is JSON, not a <see cref="MockupDocument"/>.</b> A document exists only when every
/// rule holds, and a value edit is allowed to leave the tree incomplete (see <see cref="MockupEdit"/>), so the
/// session holds the tree and offers <see cref="Document"/> whenever it is valid. Its text is
/// <see cref="MockupWriter"/>'s canonical form whenever it is valid, so <see cref="IsDirty"/> means "not the
/// same canonical bytes as the tree that was opened": setting a value and setting it back is clean again.
/// </para>
/// <para>
/// Undo applies the edit's stored inverse, which restores exactly what the edit changed and is never judged
/// again; redo applies the edit itself. A new edit clears the redo stack.
/// </para>
/// </summary>
public sealed class MockupEditSession
{
    // The reader's depth (#197): a tree nested the 32 levels the schema allows is about 70 JSON levels deep (an
    // object and a children list per level), past System.Text.Json's default of 64, so without this a tree the
    // reader accepts could not be opened for editing at all. Declared first: Indented reads it.
    private static readonly JsonDocumentOptions Deep = new() { MaxDepth = 256 };

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = Deep.MaxDepth,
    };

    private string opened;
    private readonly Stack<(MockupEdit Edit, MockupEdit Inverse)> undo = new();
    private readonly Stack<MockupEdit> redo = new();
    private JsonObject tree;

    private MockupEditSession(JsonObject tree)
    {
        this.tree = tree;
        Evaluate();
        opened = Json;

        // A valid tree is edited in its canonical form, so what the editor shows in any order (the data sets,
        // a node's properties) is the order it will be saved in. An invalid one stays as it was written.
        if (Document is not null)
        {
            this.tree = (JsonObject)JsonNode.Parse(Json, documentOptions: Deep)!;
        }
    }

    /// <summary>The tree as it is now: canonical when it is valid, otherwise as it stands.</summary>
    public string Json { get; private set; } = "";

    /// <summary>The tree as a document, when the reader accepts it; null while it is incomplete.</summary>
    public MockupDocument? Document { get; private set; }

    /// <summary>Everything the reader says is wrong with the tree now. Shown, never hidden.</summary>
    public IReadOnlyList<MockupError> Errors { get; private set; } = [];

    /// <summary>True when the tree is not the one that was opened.</summary>
    public bool IsDirty => !string.Equals(Json, opened, StringComparison.Ordinal);

    /// <summary>The tree under edit, for what reads it in this assembly (the outline). Never handed out to change.</summary>
    internal JsonObject Tree => tree;

    /// <summary>
    /// Makes the tree as it is now the clean point (#196: it has just been saved). The undo history stays, so undoing
    /// past the save is dirty again, as it is in any editor.
    /// </summary>
    public void MarkSaved() => opened = Json;

    /// <summary>Whether there is an edit to undo.</summary>
    public bool CanUndo => undo.Count > 0;

    /// <summary>Whether there is an undone edit to redo.</summary>
    public bool CanRedo => redo.Count > 0;

    /// <summary>Opens a document, which is valid by construction.</summary>
    public static MockupEditSession Open(MockupDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new MockupEditSession((JsonObject)JsonNode.Parse(MockupWriter.Write(document), documentOptions: Deep)!);
    }

    /// <summary>
    /// Opens stored text, which an agent or a person may have written and the reader may refuse: a tree with
    /// errors opens, so it can be repaired, and its errors are on <see cref="Errors"/>. What does not open is
    /// text that is no tree to edit at all (not JSON, not an object, a duplicated property, or not a mockup of
    /// this version), and the refusal is the reader's.
    /// </summary>
    public static bool TryOpen(string json, [NotNullWhen(true)] out MockupEditSession? session, out IReadOnlyList<MockupError> refusal)
    {
        ArgumentNullException.ThrowIfNull(json);
        session = null;

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: Deep) as JsonObject;
            Materialise(root);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            root = null;
        }

        var opensAsThisVersion = root?["mockup"] is JsonValue version
            && version.GetValueKind() == JsonValueKind.Number
            && version.TryGetValue<int>(out var number)
            && number == MockupVocabulary.Version;

        if (root is null || !opensAsThisVersion)
        {
            refusal = MockupReader.Read(json).Errors;
            if (refusal.Count == 0)
            {
                refusal = [new MockupError("$", "This cannot be opened for editing.")];
            }

            return false;
        }

        session = new MockupEditSession(root);
        refusal = [];
        return true;
    }

    /// <summary>Applies an edit, or refuses it and changes nothing.</summary>
    public MockupEditOutcome Apply(MockupEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);

        var (outcome, result) = Judge(edit);
        if (result is not { } accepted)
        {
            return outcome;
        }

        tree = accepted.Tree;
        (Json, Document, Errors) = (accepted.Json, accepted.Document, accepted.Errors);
        undo.Push((edit, accepted.Inverse));
        redo.Clear();
        return outcome;
    }

    /// <summary>
    /// What <see cref="Apply"/> would answer, without applying anything (#197: the palette offers only the places
    /// whose insert the reader accepts, and says why for the others in the same words Apply would).
    /// </summary>
    public MockupEditOutcome Check(MockupEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return Judge(edit).Outcome;
    }

    private (MockupEditOutcome Outcome, (JsonObject Tree, string Json, MockupDocument? Document, IReadOnlyList<MockupError> Errors, MockupEdit Inverse)? Result) Judge(MockupEdit edit)
    {
        var next = (JsonObject)tree.DeepClone();
        MockupApplied applied;
        try
        {
            applied = edit.Apply(next);
        }
        catch (MockupEditRefusedException refused)
        {
            return (new MockupEditOutcome(false, [refused.Error]), null);
        }

        var (json, document, errors) = Evaluate(next);
        var introduced = edit.Kind == MockupEditKind.Value
            ? WrittenWrong(errors, applied.Locations)
            : Introduced(errors);

        return introduced.Count > 0
            ? (new MockupEditOutcome(false, introduced), null)
            : (MockupEditOutcome.Done, (next, json, document, errors, applied.Inverse));
    }

    /// <summary>Undoes the last edit. False when there is none.</summary>
    public bool Undo()
    {
        if (!undo.TryPop(out var last))
        {
            return false;
        }

        last.Inverse.Apply(tree);
        Evaluate();
        redo.Push(last.Edit);
        return true;
    }

    /// <summary>Redoes the last undone edit. False when there is none.</summary>
    public bool Redo()
    {
        if (!redo.TryPop(out var edit))
        {
            return false;
        }

        // It was accepted against this very tree before it was undone, so it is applied without judging again.
        var applied = edit.Apply(tree);
        Evaluate();
        undo.Push((edit, applied.Inverse));
        return true;
    }

    /// <summary>
    /// The errors a structural edit introduced: those whose sentence the tree before did not carry as often.
    /// Compared by sentence rather than path because a structural edit moves paths.
    /// </summary>
    private List<MockupError> Introduced(IReadOnlyList<MockupError> after)
    {
        var before = Errors.GroupBy(e => e.Message, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var introduced = new List<MockupError>();
        foreach (var error in after)
        {
            if (before.TryGetValue(error.Message, out var count) && count > 0)
            {
                before[error.Message] = count - 1;
            }
            else
            {
                introduced.Add(error);
            }
        }

        return introduced;
    }

    /// <summary>The errors at or under what a value edit wrote that the tree before did not have.</summary>
    private List<MockupError> WrittenWrong(IReadOnlyList<MockupError> after, IReadOnlyList<string> locations)
    {
        var before = Errors.Select(e => e.ToString()).ToHashSet(StringComparer.Ordinal);
        return after
            .Where(e => locations.Any(location => e.Path == location
                    || e.Path.StartsWith(location + ".", StringComparison.Ordinal)
                    || e.Path.StartsWith(location + "[", StringComparison.Ordinal))
                && !before.Contains(e.ToString()))
            .ToList();
    }

    /// <summary>
    /// Builds every object's properties now. A parsed node does that lazily, and a duplicated property throws
    /// when it does, so without this the throw would come from the middle of a later edit.
    /// </summary>
    private static void Materialise(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject value:
                foreach (var property in value)
                {
                    Materialise(property.Value);
                }

                break;
            case JsonArray list:
                foreach (var item in list)
                {
                    Materialise(item);
                }

                break;
        }
    }

    private void Evaluate() => (Json, Document, Errors) = Evaluate(tree);

    private static (string Json, MockupDocument? Document, IReadOnlyList<MockupError> Errors) Evaluate(JsonObject tree)
    {
        var text = tree.ToJsonString(Indented) + "\n";
        var read = MockupReader.Read(text);
        return read.Document is { } document
            ? (MockupWriter.Write(document), document, [])
            : (text, null, read.Errors);
    }
}
