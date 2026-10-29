using Fravashi.Application.Wiki;
using Fravashi.Domain.Projects;

namespace Fravashi.Infrastructure.Tests;

/// <summary>
/// For a test with ONE project (backlog 121): whichever project is asked for, it is this library. The product
/// never does this; it asks <c>SqliteWikiLibraries</c> for the project a window or a session really belongs to.
/// </summary>
internal sealed class OneWiki(IWikiLibrary library) : IWikiLibraries
{
    public IWikiLibrary For(ProjectId project) => library;

    public IWikiDocuments Documents => library;
}
