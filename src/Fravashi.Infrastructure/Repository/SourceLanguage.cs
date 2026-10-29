namespace Fravashi.Infrastructure.Repository;

/// <summary>Maps a file path to a syntax-highlighting language hint (shared by the source explorer + git reads).</summary>
public static class SourceLanguage
{
    public static string For(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp",
        ".razor" or ".cshtml" or ".html" or ".htm" => "html",
        ".csproj" or ".props" or ".targets" or ".xml" or ".slnx" => "xml",
        ".js" or ".mjs" => "javascript",
        ".ts" => "typescript",
        ".json" => "json",
        ".css" => "css",
        ".sql" => "sql",
        ".ps1" or ".psm1" => "powershell",
        ".sh" or ".bash" => "shell",
        ".py" => "python",
        ".java" => "java",
        ".md" or ".markdown" => "markdown",
        ".yml" or ".yaml" => "yaml",
        _ => string.Empty,
    };
}
