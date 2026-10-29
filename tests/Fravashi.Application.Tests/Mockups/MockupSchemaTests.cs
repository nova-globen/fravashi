using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Xunit;

namespace Fravashi.Application.Tests.Mockups;

/// <summary>
/// The JSON Schema is a second spelling of rules the reader owns, and a second spelling is a place to drift.
/// These tests read the schema's vocabularies out of its text and hold each equal to the reader's, so a
/// component, state, event, input or property added to one and not the other fails the build.
/// </summary>
public class MockupSchemaTests
{
    private static readonly JsonElement Schema = JsonDocument.Parse(MockupSchema.Json).RootElement;

    private static JsonElement Fields => Schema.GetProperty("$defs").GetProperty("fields").GetProperty("properties");

    private static string[] Enum(JsonElement element) =>
        element.GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray();

    [Fact]
    public void The_schema_names_the_version_it_describes()
    {
        Schema.GetProperty("$id").GetString().Should().Be(MockupSchema.Id).And.EndWith($"/{MockupVocabulary.Version}");
        Schema.GetProperty("properties").GetProperty("mockup").GetProperty("const").GetInt32().Should().Be(MockupVocabulary.Version);
    }

    [Fact]
    public void The_root_properties_are_the_readers()
    {
        Schema.GetProperty("properties").EnumerateObject().Select(p => p.Name)
            .Should().Equal(MockupVocabulary.RootProperties);
    }

    [Fact]
    public void The_node_properties_are_the_readers_in_canonical_order()
    {
        Fields.EnumerateObject().Select(p => p.Name).Should().Equal(MockupVocabulary.NodeProperties);
    }

    [Fact]
    public void The_vocabularies_are_the_readers()
    {
        Enum(Fields.GetProperty("type")).Should().Equal(MockupVocabulary.Names<MockupComponent>());
        Enum(Fields.GetProperty("layout")).Should().Equal(MockupVocabulary.Names<MockupLayout>());
        Enum(Fields.GetProperty("input")).Should().Equal(MockupVocabulary.Names<MockupInput>());
        Enum(Fields.GetProperty("state")).Should().Equal(MockupVocabulary.Names<MockupState>());
        Enum(Fields.GetProperty("states").GetProperty("propertyNames")).Should().Equal(MockupVocabulary.Names<MockupState>());
        Enum(Fields.GetProperty("on").GetProperty("propertyNames")).Should().Equal(MockupVocabulary.Names<MockupEvent>());
    }

    [Fact]
    public void The_small_objects_are_the_readers()
    {
        var defs = Schema.GetProperty("$defs");
        defs.GetProperty("a11y").GetProperty("properties").EnumerateObject().Select(p => p.Name)
            .Should().Equal(MockupVocabulary.A11yProperties);
        Enum(defs.GetProperty("a11y").GetProperty("properties").GetProperty("live")).Should().Equal(MockupVocabulary.LiveValues);
        defs.GetProperty("action").GetProperty("properties").EnumerateObject().Select(p => p.Name)
            .Should().Equal(MockupVocabulary.ActionProperties);
        Schema.GetProperty("properties").GetProperty("viewports").GetProperty("items").GetProperty("properties")
            .EnumerateObject().Select(p => p.Name).Should().Equal(MockupVocabulary.ViewportProperties);
    }

    [Fact]
    public void An_override_refuses_exactly_what_the_reader_refuses()
    {
        Schema.GetProperty("$defs").GetProperty("override").GetProperty("properties").EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.False)
            .Select(p => p.Name)
            .Should().BeEquivalentTo(MockupVocabulary.NotInOverride);
    }

    [Fact]
    public void Every_component_has_a_property_set_and_carries_only_known_properties()
    {
        foreach (var component in System.Enum.GetValues<MockupComponent>())
        {
            MockupVocabulary.PropertiesOf(component).Should().BeSubsetOf(MockupVocabulary.NodeProperties);
            MockupVocabulary.PropertiesOf(component).Contains("children")
                .Should().Be(MockupVocabulary.Containers.Contains(component), $"{component} holds children only if it is a container");
        }
    }
}
