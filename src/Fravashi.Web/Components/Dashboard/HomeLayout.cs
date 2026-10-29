using System.Text.Json;
using System.Text.Json.Serialization;
using Fravashi.Application.Persistence;
using Fravashi.Domain;

namespace Fravashi.Web.Components.Dashboard;

/// <summary>
/// Work item #360: one card on Home and where it sits. <see cref="Width"/> is in columns of the
/// <see cref="HomeLayout.Columns"/>-column grid, and a hidden card is not rendered at all outside the arrange mode.
/// <see cref="Height"/> is a size HINT in rows, not a minimum (work item #363): a card is as tall as its content, the
/// cards in a row line up with the tallest of them, and the height says how far a card's list (recent dispatches,
/// live sessions, recent conversations) may grow before it scrolls inside the card. A taller card still spans that
/// many grid rows, so shorter cards can stack beside it; it never stretches its border around empty space.
/// </summary>
public sealed record HomeCardPlacement(string Id, int Width, int Height, bool Hidden);

/// <summary>A card Home knows how to draw: its stable id (what a stored layout names) and the name the arrange mode shows.</summary>
public sealed record HomeCardKind(string Id, string Name);

/// <summary>
/// Work item #360: the cards on Home, in order, with their sizes. Immutable: every move answers a new layout, so a
/// move is one value the page saves once. The stored form is a small readable JSON document (<see cref="ToJson"/>),
/// and reading one never fails (<see cref="Parse"/>): a card id this build does not know is dropped and a card the
/// document does not name is put back at its default place, so a later release that adds a card never breaks a
/// saved layout.
/// </summary>
public sealed class HomeLayout
{
    /// <summary>The grid's column count. A card is 2 to 12 columns wide; one column is too narrow to read.</summary>
    public const int Columns = 12;

    public const int MinWidth = 2;

    public const int MinHeight = 1;

    public const int MaxHeight = 8;

    /// <summary>The document's shape version, written so a later change of shape can tell an old document apart.</summary>
    public const int DocumentVersion = 1;

    // The card ids. Stable on purpose: they are what a stored layout and a shipped default name.
    public const string Conversation = "conversation";
    public const string Waiting = "waiting";
    public const string Updates = "updates";
    public const string Wiki = "wiki";
    public const string LiveSessions = "live-sessions";
    public const string Progress = "progress";
    public const string Autopilot = "autopilot";
    public const string RecentDispatches = "recent-dispatches";
    public const string AgentSpend = "agent-spend";
    public const string Throughput = "throughput";
    public const string TimeSpent = "time-spent";
    public const string RepositoryHealth = "repository-health";
    public const string RecentConversations = "recent-conversations";

    /// <summary>Every card Home can draw, with the name the arrange mode gives it.</summary>
    public static readonly IReadOnlyList<HomeCardKind> Kinds =
    [
        new(Conversation, $"{ProductName.Short} assistant"),
        new(Waiting, "Waiting for you"),
        new(Updates, "Updates"),
        new(Wiki, "Wiki"),
        new(LiveSessions, "Live agent sessions"),
        new(Progress, "Progress"),
        new(Autopilot, "Autopilot"),
        new(RecentDispatches, "Recent dispatches"),
        new(AgentSpend, "Agent spend"),
        new(Throughput, "Throughput"),
        new(TimeSpent, "Where time goes"),
        new(RepositoryHealth, "Repository health"),
        new(RecentConversations, "Recent conversations"),
    ];

    /// <summary>
    /// THE built-in default, and the only place it is written. It is the arrangement the owner made on Home and handed
    /// over with "Copy layout" on 2026-10-23 (work item #363, replacing #360's and #362's): the conversation box; the
    /// autopilot beside Progress; the recent dispatches beside the live sessions, both four rows so their lists show
    /// more before they scroll; Waiting for you, Updates and the wiki in thirds; the recent conversations beside where
    /// the time goes; and the other three insight cards in thirds. Every row is filled edge to edge.
    /// </summary>
    public const string BuiltInDefaultDocument = """
        {
          "version": 1,
          "columns": 12,
          "cards": [
            { "id": "conversation", "width": 12, "height": 1, "hidden": false },
            { "id": "autopilot", "width": 6, "height": 1, "hidden": false },
            { "id": "progress", "width": 6, "height": 1, "hidden": false },
            { "id": "recent-dispatches", "width": 6, "height": 4, "hidden": false },
            { "id": "live-sessions", "width": 6, "height": 4, "hidden": false },
            { "id": "waiting", "width": 4, "height": 1, "hidden": false },
            { "id": "updates", "width": 4, "height": 1, "hidden": false },
            { "id": "wiki", "width": 4, "height": 1, "hidden": false },
            { "id": "recent-conversations", "width": 8, "height": 1, "hidden": false },
            { "id": "time-spent", "width": 4, "height": 1, "hidden": false },
            { "id": "agent-spend", "width": 4, "height": 1, "hidden": false },
            { "id": "throughput", "width": 4, "height": 1, "hidden": false },
            { "id": "repository-health", "width": 4, "height": 1, "hidden": false }
          ]
        }
        """;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly Lazy<HomeLayout> DefaultLayout = new(() =>
    {
        var parsed = ReadDocument(BuiltInDefaultDocument)
            ?? throw new InvalidOperationException("The built-in Home layout does not parse.");
        if (parsed.Count != Kinds.Count || Kinds.Any(k => parsed.All(p => p.Id != k.Id)))
        {
            throw new InvalidOperationException("The built-in Home layout must name every card exactly once.");
        }

        return new HomeLayout(parsed);
    });

    private HomeLayout(IReadOnlyList<HomeCardPlacement> cards) => Cards = cards;

    /// <summary>The built-in default (<see cref="BuiltInDefaultDocument"/>).</summary>
    public static HomeLayout Default => DefaultLayout.Value;

    public IReadOnlyList<HomeCardPlacement> Cards { get; }

    public IEnumerable<HomeCardPlacement> Visible => Cards.Where(c => !c.Hidden);

    public IEnumerable<HomeCardPlacement> HiddenCards => Cards.Where(c => c.Hidden);

    public static string NameOf(string id) => Kinds.FirstOrDefault(k => k.Id == id)?.Name ?? id;

    /// <summary>
    /// A stored document as a layout, never failing: blank or unreadable is the default; an unknown id or a repeated
    /// one is dropped; sizes are clamped; a known card the document does not name is inserted after the card that
    /// precedes it in the default (or before the one that follows it, when none precedes it), with its default size.
    /// </summary>
    public static HomeLayout Parse(string? document)
    {
        var read = string.IsNullOrWhiteSpace(document) ? null : ReadDocument(document);
        return read is null ? Default : Reconcile(read);
    }

    /// <summary>The readable JSON document the settings table holds and "Copy layout" hands over.</summary>
    public string ToJson() => JsonSerializer.Serialize(
        new LayoutDocument
        {
            Version = DocumentVersion,
            Columns = Columns,
            Cards = Cards.Select(c => (CardDocument?)new CardDocument { Id = c.Id, Width = c.Width, Height = c.Height, Hidden = c.Hidden }).ToList(),
        },
        WriteOptions);

    public HomeCardPlacement? Find(string id) => Cards.FirstOrDefault(c => c.Id == id);

    /// <summary>One place earlier (negative) or later (positive) among the visible cards; hidden cards keep their places.</summary>
    public HomeLayout MoveBy(string id, int places)
    {
        var visible = Visible.ToList();
        var from = visible.FindIndex(c => c.Id == id);
        if (from < 0 || places == 0)
        {
            return this;
        }

        var to = Math.Clamp(from + places, 0, visible.Count - 1);
        return to == from ? this : MoveTo(id, visible[to].Id);
    }

    /// <summary>
    /// About one row up (negative) or down (positive): past as many visible cards as fill the grid's width, so a
    /// half-width card moves past two half-width cards and a full-width one past one.
    /// </summary>
    public HomeLayout MoveRow(string id, int direction)
    {
        var visible = Visible.ToList();
        var from = visible.FindIndex(c => c.Id == id);
        if (from < 0 || direction == 0)
        {
            return this;
        }

        var step = Math.Sign(direction);
        var passed = 0;
        var places = 0;
        for (var i = from + step; i >= 0 && i < visible.Count && passed < Columns; i += step)
        {
            passed += visible[i].Width;
            places += step;
        }

        return MoveBy(id, places);
    }

    /// <summary>
    /// Drag and drop: the dragged card takes the target's place, the target and what lies between shifting one place
    /// toward where the dragged card was (before the target when dragged up, after it when dragged down).
    /// </summary>
    public HomeLayout MoveTo(string id, string targetId)
    {
        if (id == targetId)
        {
            return this;
        }

        var list = Cards.ToList();
        var from = list.FindIndex(c => c.Id == id);
        var to = list.FindIndex(c => c.Id == targetId);
        if (from < 0 || to < 0)
        {
            return this;
        }

        var card = list[from];
        list.RemoveAt(from);
        list.Insert(to, card);
        return new HomeLayout(list);
    }

    public HomeLayout Resize(string id, int width, int height) =>
        Replace(id, c => c with { Width = ClampWidth(width), Height = ClampHeight(height) });

    public HomeLayout SetHidden(string id, bool hidden) => Replace(id, c => c with { Hidden = hidden });

    public static int ClampWidth(int width) => Math.Clamp(width, MinWidth, Columns);

    public static int ClampHeight(int height) => Math.Clamp(height, MinHeight, MaxHeight);

    private HomeLayout Replace(string id, Func<HomeCardPlacement, HomeCardPlacement> change)
    {
        var list = Cards.ToList();
        var at = list.FindIndex(c => c.Id == id);
        if (at < 0)
        {
            return this;
        }

        var changed = change(list[at]);
        if (changed == list[at])
        {
            return this;
        }

        list[at] = changed;
        return new HomeLayout(list);
    }

    private static HomeLayout Reconcile(IReadOnlyList<HomeCardPlacement> read)
    {
        var known = Kinds.Select(k => k.Id).ToHashSet(StringComparer.Ordinal);
        var result = new List<HomeCardPlacement>();
        foreach (var card in read)
        {
            if (known.Contains(card.Id) && result.All(c => c.Id != card.Id))
            {
                result.Add(card);
            }
        }

        var defaults = Default.Cards;
        for (var i = 0; i < defaults.Count; i++)
        {
            if (result.Any(c => c.Id == defaults[i].Id))
            {
                continue;
            }

            // Right after the nearest card that precedes it in the default; with none, right before the nearest that
            // follows it; with neither (an empty document), last.
            var insertAt = -1;
            for (var before = i - 1; before >= 0 && insertAt < 0; before--)
            {
                var at = result.FindIndex(c => c.Id == defaults[before].Id);
                insertAt = at >= 0 ? at + 1 : -1;
            }

            for (var after = i + 1; after < defaults.Count && insertAt < 0; after++)
            {
                insertAt = result.FindIndex(c => c.Id == defaults[after].Id);
            }

            result.Insert(insertAt < 0 ? result.Count : insertAt, defaults[i]);
        }

        return new HomeLayout(result);
    }

    private static List<HomeCardPlacement>? ReadDocument(string document)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<LayoutDocument>(document, ReadOptions);
            if (parsed?.Cards is null)
            {
                return null;
            }

            return parsed.Cards
                .Where(c => !string.IsNullOrWhiteSpace(c?.Id))
                .Select(c => new HomeCardPlacement(c!.Id!.Trim(), ClampWidth(c.Width ?? Columns), ClampHeight(c.Height ?? MinHeight), c.Hidden ?? false))
                .ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class LayoutDocument
    {
        [JsonPropertyOrder(0)]
        public int Version { get; set; }

        [JsonPropertyOrder(1)]
        public int Columns { get; set; }

        [JsonPropertyOrder(2)]
        public List<CardDocument?>? Cards { get; set; }
    }

    private sealed class CardDocument
    {
        [JsonPropertyOrder(0)]
        public string? Id { get; set; }

        [JsonPropertyOrder(1)]
        public int? Width { get; set; }

        [JsonPropertyOrder(2)]
        public int? Height { get; set; }

        [JsonPropertyOrder(3)]
        public bool? Hidden { get; set; }
    }
}

/// <summary>
/// Work item #360: where Home's arrangement lives. The settings table is this install's (one person, one machine,
/// one data directory), and Home's other per-install choices are already there (the default models, the
/// concurrency cap), so the layout is one more row: <see cref="Key"/>, holding <see cref="HomeLayout.ToJson"/>.
/// Reset removes the row, so a later shipped default reaches a person who has not arranged anything since.
/// </summary>
public sealed class HomeLayoutStore(IConfigurationStore store, ILogger<HomeLayoutStore> logger)
{
    public const string Key = "Home:Layout";

    public HomeLayout Load()
    {
        try
        {
            return HomeLayout.Parse(store.Get(Key));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "The Home layout could not be read; showing the default.");
            return HomeLayout.Default;
        }
    }

    /// <summary>Saves the layout; false (and a log line) when the settings table refused it.</summary>
    public bool Save(HomeLayout layout)
    {
        try
        {
            store.Set(Key, layout.ToJson());
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "The Home layout could not be saved.");
            return false;
        }
    }

    public bool Reset()
    {
        try
        {
            store.Remove(Key);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "The Home layout could not be reset.");
            return false;
        }
    }
}
