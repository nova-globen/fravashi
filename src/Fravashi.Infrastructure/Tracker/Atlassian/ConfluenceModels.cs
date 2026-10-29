using System.Text.Json.Serialization;

namespace Fravashi.Infrastructure.Tracker.Atlassian;

// ── v2 API response wrappers ─────────────────────────────────────────────────

public class ConfluenceSpaceResult
{
    [JsonPropertyName("results")]
    public List<ConfluenceSpace> Results { get; set; } = [];
}

public class ConfluenceSpace
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}

public class ConfluencePageResult
{
    [JsonPropertyName("results")]
    public List<ConfluencePage> Results { get; set; } = [];
}

public class ConfluencePage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("spaceId")]
    public string SpaceId { get; set; } = string.Empty;

    [JsonPropertyName("parentId")]
    public string? ParentId { get; set; }

    [JsonPropertyName("parentType")]
    public string? ParentType { get; set; }

    [JsonPropertyName("authorId")]
    public string AuthorId { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("version")]
    public ConfluenceVersion? Version { get; set; }

    [JsonPropertyName("body")]
    public ConfluenceBody? Body { get; set; }

    [JsonPropertyName("_links")]
    public ConfluenceLinks? Links { get; set; }

    // Not returned by v2 API - populated by service when needed
    public ConfluenceSpace? Space { get; set; }
}

public class ConfluenceVersion
{
    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("authorId")]
    public string? AuthorId { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

public class ConfluenceBody
{
    [JsonPropertyName("storage")]
    public ConfluenceStorage? Storage { get; set; }

    [JsonPropertyName("atlas_doc_format")]
    public ConfluenceStorage? AtlasDocFormat { get; set; }
}

public class ConfluenceStorage
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("representation")]
    public string Representation { get; set; } = string.Empty;
}

public class ConfluenceLinks
{
    [JsonPropertyName("webui")]
    public string? WebUi { get; set; }
}
