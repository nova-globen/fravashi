using System.Text.Json;
using Fravashi.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace Fravashi.Infrastructure.Integrity;

/// <summary>
/// Reads the launcher-written expected-shape record from the mounted data directory
/// (<c>integrity-record.json</c>) - the app side of the F3 / ADR 0007 contract. A missing file means the launcher
/// did not provision integrity for this container (a pre-T8 container, or a host-dev run): the reader returns null
/// and the evaluator downgrades to report-only. A malformed file also returns null rather than crashing.
/// </summary>
public sealed class FileIntegrityRecordReader
{
    /// <summary>The file the launcher writes into the data directory.</summary>
    public const string FileName = "integrity-record.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly ILogger<FileIntegrityRecordReader> _logger;

    public FileIntegrityRecordReader(DataDirectory dataDirectory, ILogger<FileIntegrityRecordReader> logger)
    {
        _path = Path.Combine(dataDirectory.Path, FileName);
        _logger = logger;
    }

    /// <summary>The parsed record, or null when absent/malformed (⇒ report-only).</summary>
    public IntegrityRecordModel? Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<IntegrityRecordModel>(File.ReadAllText(_path), Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogDebug(ex, "Could not read the integrity record at {Path}.", _path);
            return null;
        }
    }
}
