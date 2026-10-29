using Fravashi.Web.Services;
using Xunit;

namespace Fravashi.Web.Tests;

public class FileIconCatalogTests
{
    // The shape of the vendored material-icons.json, reduced to a handful of entries.
    private const string Manifest = """
        {
          "iconDefinitions": {
            "csharp": { "iconPath": "./../icons/csharp.svg" },
            "test-ts": { "iconPath": "./../icons/test-ts.svg" },
            "typescript": { "iconPath": "./../icons/typescript.svg" },
            "readme": { "iconPath": "./../icons/readme.svg" },
            "file": { "iconPath": "./../icons/file.svg" },
            "folder": { "iconPath": "./../icons/folder.svg" },
            "folder-open": { "iconPath": "./../icons/folder-open.svg" },
            "folder-src": { "iconPath": "./../icons/folder-src.svg" },
            "folder-src-open": { "iconPath": "./../icons/folder-src-open.svg" }
          },
          "fileExtensions": { "cs": "csharp", "ts": "typescript", "spec.ts": "test-ts" },
          "fileNames": { "readme.md": "readme" },
          "folderNames": { "src": "folder-src" },
          "folderNamesExpanded": { "src": "folder-src-open" },
          "file": "file",
          "folder": "folder",
          "folderExpanded": "folder-open"
        }
        """;

    private static FileIconCatalog Catalog => FileIconCatalog.FromJson(Manifest);

    [Fact]
    public void Exact_file_name_wins_over_the_extension()
    {
        Assert.Equal("vendor/material-icons/icons/readme.svg", Catalog.FileIconUrl("README.md"));
    }

    [Fact]
    public void Longest_extension_suffix_wins()
    {
        Assert.Equal("vendor/material-icons/icons/test-ts.svg", Catalog.FileIconUrl("thing.spec.ts"));
        Assert.Equal("vendor/material-icons/icons/typescript.svg", Catalog.FileIconUrl("thing.ts"));
    }

    [Fact]
    public void Simple_extension_maps_case_insensitively()
    {
        Assert.Equal("vendor/material-icons/icons/csharp.svg", Catalog.FileIconUrl("Program.CS"));
    }

    [Fact]
    public void Unknown_file_falls_back_to_the_default_icon()
    {
        Assert.Equal("vendor/material-icons/icons/file.svg", Catalog.FileIconUrl("mystery.zzz"));
        Assert.Equal("vendor/material-icons/icons/file.svg", Catalog.FileIconUrl("no-extension"));
    }

    [Fact]
    public void Known_folder_gets_its_named_icon_with_an_expanded_variant()
    {
        Assert.Equal("vendor/material-icons/icons/folder-src.svg", Catalog.FolderIconUrl("src", expanded: false));
        Assert.Equal("vendor/material-icons/icons/folder-src-open.svg", Catalog.FolderIconUrl("src", expanded: true));
    }

    [Fact]
    public void Unknown_folder_falls_back_to_the_default_folder_icons()
    {
        Assert.Equal("vendor/material-icons/icons/folder.svg", Catalog.FolderIconUrl("whatever", expanded: false));
        Assert.Equal("vendor/material-icons/icons/folder-open.svg", Catalog.FolderIconUrl("whatever", expanded: true));
    }
}
