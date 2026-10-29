using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fravashi.Web.Services;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 176: one keystroke in a long description is one message carrying the whole text, and past the circuit's
/// limit the server closes the connection. Held against the longest item of the synthetic backlog (as long as the
/// longest a real backlog carried, about 30,000 characters), framed the way an input
/// event travels: the text TWICE (the event's value, and the element's field value Blazor sends to keep a binding in
/// step), each serialized as JSON, and that JSON carried as a string inside the hub's own JSON, escaped the way the
/// browser's JSON.stringify escapes (non-ASCII left as it is).
/// </summary>
public class CircuitLimitsTests
{
    private static readonly JsonSerializerOptions AsTheBrowserWrites = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public void The_longest_description_in_this_repository_fits_in_one_message_with_room_to_grow()
    {
        var longest = LongestItemBody();
        var descriptor = JsonSerializer.Serialize(
            new { eventHandlerId = 1, eventName = "input", eventFieldInfo = new { componentId = 1, fieldValue = longest } },
            AsTheBrowserWrites);
        var eventArgs = JsonSerializer.Serialize(new { value = longest }, AsTheBrowserWrites);
        var hubMessage = JsonSerializer.Serialize(
            new { type = 1, target = "DispatchBrowserEvent", arguments = new[] { descriptor, eventArgs } }, AsTheBrowserWrites);
        var bytes = Encoding.UTF8.GetByteCount(hubMessage);

        Assert.True(bytes > 32 * 1024, $"the default limit is what broke: the longest item frames to {bytes} bytes");
        Assert.True(
            bytes * 10 < CircuitLimits.MaximumReceiveMessageBytes,
            $"a description ten times this repository's longest ({bytes} bytes framed) must still fit in one message");
    }

    private static string LongestItemBody()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fravashi.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var backlog = File.ReadAllText(Path.Combine(
            dir!.FullName, "tests", "Fravashi.Infrastructure.Tests", "Migration", "Fixtures", "synthetic-backlog.md"));
        return Regex.Split(backlog, @"^### \d+\. ", RegexOptions.Multiline).MaxBy(section => section.Length)!;
    }
}
