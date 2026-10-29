using AwesomeAssertions;
using Fravashi.Application.Embeddings;
using Fravashi.Application.Wiki;
using Fravashi.Domain.Wiki;
using Fravashi.Infrastructure.Embeddings;
using Fravashi.Infrastructure.Tests.Embeddings;
using Fravashi.Infrastructure.Tests.Persistence;
using Fravashi.Infrastructure.Wiki;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Wiki;

/// <summary>
/// Backlog 40's index. The mechanics (what is indexed, when it is re-indexed, what is returned and what is
/// not) run everywhere against an embedder that only knows words. Whether it finds by MEANING is a claim
/// about the real model, so that one test runs against it and skips with its reason when there is none.
/// </summary>
public sealed class SqliteWikiMeaningIndexTests : IDisposable
{
    private readonly SqliteTestContext _context = new();
    private readonly SqliteWikiLibrary _wiki;

    public SqliteWikiMeaningIndexTests() => _wiki = new SqliteWikiLibrary(_context.Factory, TimeProvider.System, Fravashi.Domain.Projects.ProjectId.Default);

    public void Dispose() => _context.Dispose();

    private SqliteWikiMeaningIndex Index(ITextEmbedder embedder) => new(_context.Factory, embedder, _wiki);

    /// <summary>
    /// An index with every document already embedded, for a test about what a search RETURNS. A search indexes what
    /// changed on its own two second budget and the first document always runs, so a test that searched straight
    /// after creating several documents was asserting how fast the machine was: on a loaded CI runner the newer
    /// document used the budget, the one the test was about waited for the next search, and the answer was
    /// correctly empty (run 244, 2026-09-27). The budget has its own test,
    /// <see cref="What_could_not_be_indexed_in_time_is_counted_and_said"/>.
    /// </summary>
    private async Task<SqliteWikiMeaningIndex> IndexedAsync(ITextEmbedder embedder)
    {
        var index = Index(embedder);
        (await index.CatchUpAsync(TimeSpan.FromMinutes(1))).Should().Be(0);
        return index;
    }

    private async Task<WikiItem> DocumentAsync(string title, string body, WikiItemType type = WikiItemType.Reference)
    {
        var created = await _wiki.CreateAsync(new NewWikiItem { Type = type, Title = title, Body = body });
        created.IsValid.Should().BeTrue();
        return created.Value;
    }

    [Fact]
    public async Task With_no_model_the_answer_is_null_so_the_caller_knows_to_fall_back()
    {
        await DocumentAsync("Anything", "text");
        var index = Index(new WordsOnly { Available = false });

        index.IsAvailable.Should().BeFalse();
        (await index.FindAsync(Fravashi.Domain.Projects.ProjectId.Default, "anything", null, 10)).Should().BeNull();
    }

    [Fact]
    public async Task The_relevant_document_comes_first_and_an_irrelevant_one_is_not_returned_at_all()
    {
        var plan = await DocumentAsync("Rollout plan", "staging first then production rollout");
        await DocumentAsync("Brand", "colours logo typeface");

        var found = await (await IndexedAsync(new WordsOnly())).FindAsync(Fravashi.Domain.Projects.ProjectId.Default, "production rollout", null, 10);

        found!.Items.Select(i => i.Id).Should().Equal(plan.Id);
        found.NotYetIndexed.Should().Be(0);
    }

    [Fact]
    public async Task A_search_ranks_only_the_documents_of_the_project_it_was_asked_for()
    {
        // Backlog 121. The index is per document and knows no project; a search that ranked all of it would
        // hand one project's documents to another project's agent.
        var second = new Fravashi.Infrastructure.Persistence.SqliteProjectStore(_context.Factory, TimeProvider.System).Create("Second").Id;
        var elsewhere = new SqliteWikiLibrary(_context.Factory, TimeProvider.System, second);
        var mine = await DocumentAsync("Rollout plan", "staging first then production rollout");
        var theirs = (await elsewhere.CreateAsync(new NewWikiItem
        {
            Type = WikiItemType.Reference, Title = "Their rollout", Body = "production rollout in another project",
        })).Value;
        var index = await IndexedAsync(new WordsOnly());

        var here = await index.FindAsync(Fravashi.Domain.Projects.ProjectId.Default, "production rollout", null, 10);
        var there = await index.FindAsync(second, "production rollout", null, 10);

        here!.Items.Select(i => i.Id).Should().Equal(mine.Id);
        there!.Items.Select(i => i.Id).Should().Equal(theirs.Id);
    }

    [Fact]
    public async Task A_question_nothing_answers_gets_none_rather_than_the_least_irrelevant_document()
    {
        await DocumentAsync("Brand", "colours logo typeface");

        var found = await Index(new WordsOnly()).FindAsync(Fravashi.Domain.Projects.ProjectId.Default, "banana bread recipe", null, 10);

        found!.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task An_edited_document_is_found_by_what_it_says_now_and_no_longer_by_what_it_said()
    {
        var item = await DocumentAsync("Notes", "colours logo typeface");
        var index = Index(new WordsOnly());
        (await index.FindAsync(Fravashi.Domain.Projects.ProjectId.Default, "logo typeface", null, 10))!.Items.Should().ContainSingle();

        (await _wiki.UpdateAsync(item.Id, new WikiItemEdit { Body = "staging production rollout" }, item.Version))
            .IsValid.Should().BeTrue();

        (await index.FindAsync(Fravashi.Domain.Projects.ProjectId.Default, "production rollout", null, 10))!.Items.Should().ContainSingle();
        (await index.FindAsync(Fravashi.Domain.Projects.ProjectId.Default, "logo typeface", null, 10))!.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_deleted_document_stops_being_found_and_its_vectors_go_with_it()
    {
        var item = await DocumentAsync("Notes", "staging production rollout");
        var index = Index(new WordsOnly());
        (await index.FindAsync(Fravashi.Domain.Projects.ProjectId.Default, "production rollout", null, 10))!.Items.Should().ContainSingle();

        (await _wiki.DeleteAsync(item.Id)).Should().BeTrue();

        (await index.FindAsync(Fravashi.Domain.Projects.ProjectId.Default, "production rollout", null, 10))!.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_changed_model_reindexes_by_itself()
    {
        await DocumentAsync("Notes", "staging production rollout");
        var first = new WordsOnly { Model = "model-a" };
        await Index(first).CatchUpAsync(TimeSpan.FromSeconds(30));
        first.Calls.Should().Be(1);

        var second = new WordsOnly { Model = "model-b" };
        var waiting = await Index(second).CatchUpAsync(TimeSpan.FromSeconds(30));

        second.Calls.Should().Be(1, "vectors from another model are not comparable, so they are made again");
        waiting.Should().Be(0);

        // And once made, a document is not embedded again on every pass.
        await Index(second).CatchUpAsync(TimeSpan.FromSeconds(30));
        second.Calls.Should().Be(1);
    }

    [Fact]
    public async Task The_type_filter_and_the_limit_apply_to_what_was_found()
    {
        await DocumentAsync("Plan one", "production rollout", WikiItemType.Plan);
        await DocumentAsync("Plan two", "production rollout steps", WikiItemType.Plan);
        await DocumentAsync("A report", "production rollout", WikiItemType.Report);
        var index = await IndexedAsync(new WordsOnly());

        (await index.FindAsync(Fravashi.Domain.Projects.ProjectId.Default, "production rollout", [WikiItemType.Report], 10))!
            .Items.Should().ContainSingle().Which.Type.Should().Be(WikiItemType.Report);
        (await index.FindAsync(Fravashi.Domain.Projects.ProjectId.Default, "production rollout", [WikiItemType.Plan], 1))!.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task What_could_not_be_indexed_in_time_is_counted_and_said()
    {
        for (var i = 0; i < 4; i++)
        {
            await DocumentAsync("Doc " + i, "production rollout");
        }

        var waiting = await Index(new WordsOnly()).CatchUpAsync(TimeSpan.Zero);

        waiting.Should().Be(3, "a budget of nothing still indexes one, and says three are waiting");
    }

    [RealEmbeddingModelFact]
    public async Task With_the_real_model_the_question_item_42_measured_at_zero_matches_finds_its_document()
    {
        // 2026-09-20, live: the model asked wiki_find for "unattended change", a phrase that appears nowhere,
        // and got nothing while the document sat in the store. Neither word of the question is in this body.
        var right = await DocumentAsync(
            "Review holds",
            "When nobody is watching, an edit made by an autonomous agent is held until a person looks at it.");
        await DocumentAsync("Brand", "The colours are blue and grey, the logo is an SVG file, and the type is Inter.");
        await DocumentAsync("Backups", "The studio takes an online backup of the database while it runs.");
        using var embedder = new OnnxTextEmbedder(RealEmbeddingModelFactAttribute.Directory, "all-MiniLM-L6-v2");

        var found = await (await IndexedAsync(embedder)).FindAsync(Fravashi.Domain.Projects.ProjectId.Default, "what happens to an unattended change", null, 5);

        found!.Items.Should().NotBeEmpty();
        found.Items[0].Id.Should().Be(right.Id);
        found.Items.Select(i => i.Title).Should().NotContain("Brand");
    }

    /// <summary>
    /// Knows words, not meaning: one dimension per word it has seen. Enough to make relevance, the floor and
    /// the ordering real in a test without a 90 MB model, and nothing more is claimed from it.
    /// </summary>
    /// <summary>
    /// Backlog 40's third acceptance clause: "search returns results fast enough to be used
    /// mid-conversation, on a wiki of at least a few thousand items". That was an ESTIMATE in the item
    /// ("about two million multiply-adds per query, which is single-digit milliseconds") and an estimate
    /// is not a measurement, which is why this exists.
    /// <para>
    /// It is off by default and says so, because building a few thousand documents is seconds of work
    /// that every other run would pay for nothing. Set <c>FRAVASHI_SCALE_TEST=1</c> to run it. What it
    /// asserts is BEHAVIOUR at that size: the planted document is still found, and found first. What it
    /// REPORTS is the time, and it asserts nothing about it, because a threshold here would be measuring
    /// whatever machine happened to run it. The number belongs in an evidence file with its host named.
    /// </para>
    /// </summary>
    [ScaleFact]
    public async Task A_few_thousand_documents_are_still_searched_correctly_and_the_time_is_reported()
    {
        const int documents = 3000;
        var embedder = new FixedWidth();
        var index = Index(embedder);

        for (var i = 0; i < documents; i++)
        {
            await DocumentAsync($"Document {i}", $"filler body number {i} about nothing in particular");
        }

        // One document the query genuinely matches, so "found first" means something.
        var planted = await DocumentAsync("The needle", "a distinctive phrase nothing else here contains");
        await index.CatchUpAsync(TimeSpan.FromMinutes(5));

        const string question = "a distinctive phrase nothing else here contains";

        // COLD and WARM are different questions and the answer differs by an order of magnitude, so they
        // are measured apart. The index keeps its vectors in memory per project, so the first query after
        // a start pays for reading them all out of SQLite and every one after it does not.
        var first = System.Diagnostics.Stopwatch.StartNew();
        var found = await index.FindAsync(Fravashi.Domain.Projects.ProjectId.Default, question, null, 5);
        first.Stop();

        var warm = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var again = System.Diagnostics.Stopwatch.StartNew();
            await index.FindAsync(Fravashi.Domain.Projects.ProjectId.Default, question, null, 5);
            again.Stop();
            warm.Add(again.Elapsed.TotalMilliseconds);
        }

        // The arithmetic on its own, on this same machine, so the query's time can be ATTRIBUTED rather
        // than guessed at. If scoring is a small part of it, a vector index is not the lever it looks like.
        var vectors = new float[documents + 1][];
        var random = new Random(7);
        for (var i = 0; i < vectors.Length; i++)
        {
            vectors[i] = [.. Enumerable.Range(0, FixedWidth.Dimensions).Select(_ => (float)random.NextDouble())];
        }

        var probe = vectors[0];
        var arithmetic = System.Diagnostics.Stopwatch.StartNew();
        var best = 0f;
        foreach (var vector in vectors)
        {
            var sum = 0f;
            for (var d = 0; d < FixedWidth.Dimensions; d++)
            {
                sum += vector[d] * probe[d];
            }

            best = MathF.Max(best, sum);
        }

        arithmetic.Stop();
        best.Should().BeGreaterThan(0, "the scoring loop really ran");

        found.Should().NotBeNull();
        found!.Items.Should().NotBeEmpty($"{documents + 1} documents were indexed");
        found.Items[0].Number.Should().Be(planted.Number, "the planted document is the one that answers it");

        // Reported, never asserted. A time assertion here measures the machine, not the product.
        Console.WriteLine(
            $"[backlog 40 scale] {documents + 1} documents, {FixedWidth.Dimensions} dimensions. "
            + $"First query {first.Elapsed.TotalMilliseconds:F1} ms; "
            + $"then {string.Join(", ", warm.Select(w => w.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)))} ms. "
            + $"Scoring alone {arithmetic.Elapsed.TotalMilliseconds:F1} ms. "
            + $"Host {Environment.MachineName}, {Environment.ProcessorCount} processors, {Environment.OSVersion.VersionString}.");
    }

    /// <summary>Off unless asked for, and it says why, the way the real-model tests do.</summary>
    internal sealed class ScaleFactAttribute : FactAttribute
    {
        public ScaleFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("FRAVASHI_SCALE_TEST") != "1")
            {
                Skip = "Scale measurement: set FRAVASHI_SCALE_TEST=1 to build a few thousand documents and time a query.";
            }
        }
    }

    /// <summary>
    /// An embedder whose vectors are as WIDE as the real model's, which is the only property that decides
    /// what a linear scan costs. The values are deterministic from the text, so a query matching a document
    /// word for word scores 1 and everything else scores lower, with no model anywhere near it.
    /// </summary>
    internal sealed class FixedWidth : ITextEmbedder
    {
        public const int Dimensions = 384;

        public string Model => "fixed-width";

        public bool IsAvailable => true;

        public IReadOnlyList<float[]> Embed(string text)
        {
            var vector = new float[Dimensions];
            foreach (var token in text.ToLowerInvariant().Split(
                [' ', '\n', ',', '.'], StringSplitOptions.RemoveEmptyEntries))
            {
                vector[(uint)StringComparer.Ordinal.GetHashCode(token) % Dimensions] += 1;
            }

            var norm = MathF.Sqrt(vector.Sum(v => v * v));
            return norm == 0 ? [] : [[.. vector.Select(v => v / norm)]];
        }
    }

    internal sealed class WordsOnly : ITextEmbedder
    {
        private static readonly string[] Words =
            ["rollout", "production", "staging", "plan", "colours", "logo", "typeface", "brand", "banana", "bread", "recipe", "steps"];

        public string Model { get; init; } = "words-only";

        public bool Available { get; init; } = true;

        public int Calls { get; private set; }

        public bool IsAvailable => Available;

        public IReadOnlyList<float[]> Embed(string text)
        {
            if (!Available)
            {
                return [];
            }

            Calls++;
            var tokens = text.ToLowerInvariant().Split([' ', '\n', ',', '.'], StringSplitOptions.RemoveEmptyEntries);
            var vector = new float[Words.Length];
            for (var i = 0; i < Words.Length; i++)
            {
                vector[i] = tokens.Count(t => t == Words[i]);
            }

            var norm = MathF.Sqrt(vector.Sum(v => v * v));
            return norm == 0 ? [] : [[.. vector.Select(v => v / norm)]];
        }
    }
}
