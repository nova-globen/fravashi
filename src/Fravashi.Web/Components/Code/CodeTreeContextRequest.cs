using Fravashi.Domain.Repository;

namespace Fravashi.Web.Components.Code;

/// <summary>A right-click on a tree entry: the node plus the cursor position for the context menu.</summary>
public sealed record CodeTreeContextRequest(RepositoryNode Node, double X, double Y);
