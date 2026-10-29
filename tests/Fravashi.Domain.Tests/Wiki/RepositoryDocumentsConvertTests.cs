using System.Xml;
using AwesomeAssertions;
using Fravashi.Domain.Wiki;
using Xunit;

namespace Fravashi.Domain.Tests.Wiki;

/// <summary>
/// Documents shaped like a repository's own, converted for Confluence, are well-formed storage format. Confluence
/// refuses a page whose body is not, and the import of a repository (backlog 49) publishes all of them, so the
/// converter's unit cases are not enough on their own: whole documents mixing every block are what must go.
/// <para>
/// Until 2026-10-29 this read the repository's own <c>docs/</c>. The public source leaves that folder out,
/// so the documents are synthetic: one per shape the real ones had (an ADR, a plan with tables and code, a design
/// note with markup in prose, a reference with nested lists and quotes).
/// </para>
/// </summary>
public class RepositoryDocumentsConvertTests
{
    public static TheoryData<string, string> Documents() => new()
    {
        {
            "adr/0027-sibling-containers.md",
            """
            # ADR 0027: applications run in sibling containers

            Status: accepted. Supersedes nothing; amended by 0029 & 0031.

            ## Context

            The verifier needs to run a project's own app, and a mounted engine socket would hand an agent
            the whole engine. A `<script>` in a page, `a < b && c > d` in prose and an ampersand & must all survive.

            ## Decision

            1. The app runner owns a closed set of operations.
            2. Nothing else reaches the engine:
               - not the studio,
               - not an agent (`docker.sock` is never mounted).

            > The runner is the only door. Anything it cannot express is refused, not approximated.

            ---

            See [the plan](../plans/app-runner.md) and <https://example.test/adr>.
            """
        },
        {
            "plans/import.md",
            """
            # Importing a repository

            | Step | What moves | Where |
            |------|------------|-------|
            | 1 | `docs/backlog.md` | the tracker, item by item |
            | 2 | ADRs & plans | the wiki, as `<ac:link>`-free pages |
            | 3 | anything with a <tag> | stays a file |

            ```csharp
            var items = Parse(text).Where(i => i.Number > 0 && i.Title is not null);
            var xml = "<root><![CDATA[ nested ]]></root>";
            Dictionary<string, List<int>> map = new();
            ```

            ~~~
            an unlabelled fence with ]]> inside it
            ~~~

            **Bold**, *italic*, ~~struck~~ and `List<T>` in one line.
            """
        },
        {
            "design/shell.md",
            """
            Design note
            ===========

            The shell has three bands; the top one holds <kbd>Ctrl</kbd>+<kbd>K</kbd> and a "quoted" title's 'marks'.
            A line that ends in a backslash\
            and one with trailing spaces
            both continue the paragraph.

            * a bullet with a [link](https://example.test/?a=1&b=2)
            * another with an image ![mockup](images/shell.png)
              1. nested and ordered
              2. second

            Setext second level
            -------------------

            &copy; and &nbsp; entities, a lone & and a stray > sign.
            """
        },
        {
            "agent-reference/traps.md",
            """
            ## Traps

            > A quote
            > > with a nested quote
            > and `code` back in the outer one.

            - [ ] an unchecked task
            - [x] a checked one

            | a | b |
            |:--|--:|
            | pipe \| escaped | x |

            ```
            ]]>
            ```

            Done.
            """
        },
    };

    [Theory]
    [MemberData(nameof(Documents))]
    public void A_repository_document_converts_to_well_formed_storage(string name, string markdown)
    {
        var storage = WikiFormatConversion.ToStorage(markdown).Text;

        // The prefixes Confluence declares for its own elements, so ac: and ri: parse as they do there.
        var document = new XmlDocument();
        var load = () => document.LoadXml(
            "<root xmlns:ac=\"http://atlassian.com/content\" xmlns:ri=\"http://atlassian.com/resource/identifier\">"
            + storage + "</root>");

        load.Should().NotThrow<XmlException>(name);
    }
}
