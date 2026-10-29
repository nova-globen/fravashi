using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fravashi.Domain.Framework;

/// <summary>The transport an MCP server speaks. <c>stdio</c> = a local process; <c>http</c> = a remote endpoint.</summary>
public enum McpTransport
{
    Stdio,
    Http,
}

/// <summary>
/// One MCP <c>env</c> variable or HTTP <c>header</c>. A non-secret entry stores its plaintext <see cref="Value"/>
/// inline in the artifact payload. A <b>secret</b> entry (<see cref="IsSecret"/> = true) stores <b>no plaintext</b>
/// in the payload - the value lives in the <see cref="Application.Persistence.ISecretStore"/> under
/// <see cref="SecretRef"/>, and the serialized <c>.mcp.json</c> shows an env-expansion placeholder
/// (<c>${KEY}</c>). This keeps credentials out of the DB row and out of any exported config.
/// </summary>
/// <param name="Key">The env-var name or header name.</param>
/// <param name="Value">The plaintext value for a non-secret entry; null for a secret entry.</param>
/// <param name="IsSecret">True when the value is stored via the secret store rather than inline.</param>
/// <param name="SecretRef">The secret-store reference for a secret entry; null otherwise.</param>
public sealed record McpEnvEntry(string Key, string? Value, bool IsSecret, string? SecretRef)
{
    /// <summary>A non-secret entry with an inline value.</summary>
    public static McpEnvEntry Plain(string key, string? value) => new(key, value, false, null);

    /// <summary>The env-expansion placeholder shown in the serialized config for a secret entry.</summary>
    public string Placeholder => $"${{{Key}}}";
}

/// <summary>
/// The authoring shape of an MCP server (UI11). The Claude source-of-truth contract is the <c>.mcp.json</c>
/// server entry: for <see cref="McpTransport.Stdio"/> a <see cref="Command"/> + <see cref="Args"/> + <see cref="Env"/>;
/// for <see cref="McpTransport.Http"/> a <see cref="Url"/> + <see cref="Headers"/>. Secret-bearing env/header
/// values are stored via the secret store, never in the payload (see <see cref="McpEnvEntry"/>). Verified against
/// <c>code.claude.com/docs/en/mcp</c>. Claude-only (ADR 0005).
/// </summary>
public sealed record McpServerDefinition
{
    /// <summary>Kebab-case server name, unique among MCP servers (the <c>.mcp.json</c> key).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The transport. Load-bearing - a remote server with no transport is a config error upstream.</summary>
    public McpTransport Transport { get; init; } = McpTransport.Stdio;

    /// <summary>stdio: the executable to launch.</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>stdio: the command arguments.</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>stdio: environment variables passed to the server (secret values via the secret store).</summary>
    public IReadOnlyList<McpEnvEntry> Env { get; init; } = [];

    /// <summary>http: the endpoint URL.</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>http: HTTP headers (secret values, e.g. <c>Authorization</c>, via the secret store).</summary>
    public IReadOnlyList<McpEnvEntry> Headers { get; init; } = [];
}

/// <summary>Field validation for an <see cref="McpServerDefinition"/> - kebab name and the fields the transport needs.</summary>
public static class McpServerValidation
{
    /// <summary>Every validation failure for <paramref name="server"/>, in field order. Empty when valid.</summary>
    public static IReadOnlyList<SkillValidationError> Validate(McpServerDefinition server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var errors = new List<SkillValidationError>();

        if (!SkillValidation.IsKebabCase(server.Name))
        {
            errors.Add(new SkillValidationError(
                "name", "Name must be kebab-case (lowercase letters, digits and single hyphens), 1-64 characters."));
        }

        if (server.Transport == McpTransport.Stdio)
        {
            if (string.IsNullOrWhiteSpace(server.Command))
            {
                errors.Add(new SkillValidationError("command", "A command is required for a stdio server."));
            }

            if (server.Env.Any(e => string.IsNullOrWhiteSpace(e.Key)))
            {
                errors.Add(new SkillValidationError("env", "Every environment variable needs a name."));
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(server.Url))
            {
                errors.Add(new SkillValidationError("url", "A URL is required for an http server."));
            }

            if (server.Headers.Any(h => string.IsNullOrWhiteSpace(h.Key)))
            {
                errors.Add(new SkillValidationError("headers", "Every header needs a name."));
            }
        }

        return errors;
    }

    /// <summary>True when <paramref name="server"/> has no validation errors.</summary>
    public static bool IsValid(McpServerDefinition server) => McpServerValidation.Validate(server).Count == 0;
}

/// <summary>
/// Serializes an <see cref="McpServerDefinition"/> to its canonical <c>.mcp.json</c> fragment. Secret env/header
/// values render as env-expansion placeholders (<c>${KEY}</c>) - the plaintext is never emitted (it lives in the
/// secret store). Indented, stable JSON for the read-only preview (no file sync).
/// </summary>
public static class McpServerDocument
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Renders the <c>{ "mcpServers": { "&lt;name&gt;": { … } } }</c> fragment as indented JSON.</summary>
    public static string ToMcpJson(McpServerDefinition server)
    {
        ArgumentNullException.ThrowIfNull(server);

        var entry = new JsonObject();
        if (server.Transport == McpTransport.Stdio)
        {
            entry["type"] = "stdio";
            entry["command"] = server.Command;
            if (server.Args.Count > 0)
            {
                entry["args"] = new JsonArray([.. server.Args.Select(a => (JsonNode?)a)]);
            }

            if (server.Env.Count > 0)
            {
                entry["env"] = ToValueObject(server.Env);
            }
        }
        else
        {
            entry["type"] = "http";
            entry["url"] = server.Url;
            if (server.Headers.Count > 0)
            {
                entry["headers"] = ToValueObject(server.Headers);
            }
        }

        var root = new JsonObject
        {
            ["mcpServers"] = new JsonObject { [string.IsNullOrWhiteSpace(server.Name) ? "server" : server.Name] = entry },
        };

        return root.ToJsonString(Options);
    }

    private static JsonObject ToValueObject(IReadOnlyList<McpEnvEntry> entries)
    {
        var obj = new JsonObject();
        foreach (var e in entries.Where(e => !string.IsNullOrWhiteSpace(e.Key)))
        {
            obj[e.Key] = e.IsSecret ? e.Placeholder : e.Value ?? string.Empty;
        }

        return obj;
    }
}
