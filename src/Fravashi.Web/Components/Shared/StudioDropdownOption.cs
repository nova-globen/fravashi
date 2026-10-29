namespace Fravashi.Web.Components.Shared;

/// <summary>
/// One option in the house dropdown (backlog 24). The subtitle and the icon are what a native
/// <c>&lt;option&gt;</c> cannot carry at all: an option element renders text and nothing else, so a
/// second explanatory line or a glyph has to live in the control's title attribute, where nobody reads it.
/// </summary>
/// <param name="Value">
/// The value reported when this option is chosen. The empty string is a legal value and is how a
/// "leave it alone" option is expressed, so a caller can map it back to whatever null means to it.
/// </param>
/// <param name="Label">The primary line, and what the closed trigger shows once this option is chosen.</param>
/// <param name="Subtitle">A second, quieter line under the label, or null when the label says everything.</param>
/// <param name="Icon">
/// A short glyph or letter shown in a badge before the label, or null. Optional by design: the acceptance
/// for backlog 24 is an icon "where one exists", so an option with nothing meaningful to show gets an
/// empty slot rather than an invented symbol, and the labels stay aligned either way.
/// </param>
/// <param name="StateClass">
/// An extra CSS class on the option row, or null. It exists so a caller whose options carry a state
/// their own stylesheet already colours can reuse those rules rather than re-declaring the colours here
/// (backlog 31: the title bar's branch list has to match the Git page's branch colouring, and matching
/// by copied hex values is matching until the next edit). The dropdown itself gives it no meaning.
/// </param>
/// <param name="FilterText">
/// Extra text a filter should match on beyond the label and the subtitle, or null. A remote branch is
/// findable by its remote's name this way without the remote having to be in the visible label.
/// </param>
/// <param name="Pinned">
/// True for an ACTION that must stay in sight however long the list grows (backlog 128). A pinned option is
/// drawn in its own row that sticks to the bottom of the panel, the way the filter sticks to the top, and a
/// filter never hides it: it is not one of the things being searched, it is something to do.
/// </param>
/// <param name="Unavailable">
/// True for an option that is shown but cannot be chosen, with the reason in its subtitle (backlog 168: the model
/// picker offered a model the pinned agent CLI is refused for, and the first conversation on it died on its first
/// turn). Shown rather than hidden, so a person who knows the option exists learns why it is not on offer.
/// </param>
/// <param name="UnavailableWhy">
/// The reason for <paramref name="Unavailable"/> in a few words, for a native select that has no room for the subtitle
/// ("needs a newer agent CLI", "not on the free tier"), or null.
/// </param>
public sealed record StudioDropdownOption(
    string Value,
    string Label,
    string? Subtitle = null,
    string? Icon = null,
    string? StateClass = null,
    string? FilterText = null,
    bool Pinned = false,
    bool Unavailable = false,
    string? UnavailableWhy = null);
