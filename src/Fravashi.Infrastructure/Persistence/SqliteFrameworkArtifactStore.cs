using Fravashi.Application.Framework;
using Fravashi.Domain.Framework;
using Microsoft.Data.Sqlite;

namespace Fravashi.Infrastructure.Persistence;

/// <summary>
/// <see cref="IFrameworkArtifactStore"/> over the <c>framework_artifacts</c> + <c>framework_resources</c>
/// tables. One generic store for every <see cref="FrameworkArtifactKind"/> (skills now; the rest in T3).
/// Name uniqueness is backed by the <c>ux_framework_artifacts_kind_name</c> index and surfaced as a typed
/// <see cref="DuplicateArtifactNameException"/>; resource writes enforce the
/// <see cref="FrameworkArtifactLimits"/> cap.
/// </summary>
public sealed class SqliteFrameworkArtifactStore(ISqliteConnectionFactory factory) : IFrameworkArtifactStore
{
    private const string ArtifactColumns = "id, kind, name, payload_json, created_utc, updated_utc";
    private const string ResourceColumns = "id, artifact_id, file_name, content_type, bytes, created_utc";

    public IReadOnlyList<FrameworkArtifact> List(FrameworkArtifactKind kind)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {ArtifactColumns} FROM framework_artifacts WHERE kind = $kind ORDER BY created_utc DESC, name;";
        command.Parameters.AddWithValue("$kind", kind.ToString());
        return ReadArtifacts(command);
    }

    public FrameworkArtifact? Get(string id)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ArtifactColumns} FROM framework_artifacts WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        var rows = ReadArtifacts(command);
        return rows.Count > 0 ? rows[0] : null;
    }

    public FrameworkArtifact? GetByName(FrameworkArtifactKind kind, string name)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {ArtifactColumns} FROM framework_artifacts WHERE kind = $kind AND name = $name;";
        command.Parameters.AddWithValue("$kind", kind.ToString());
        command.Parameters.AddWithValue("$name", name);
        var rows = ReadArtifacts(command);
        return rows.Count > 0 ? rows[0] : null;
    }

    public void Add(FrameworkArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        GuardUniqueName(artifact.Kind, artifact.Name, exceptId: null);

        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"INSERT INTO framework_artifacts ({ArtifactColumns}) " +
            "VALUES ($id, $kind, $name, $payload, $created, $updated);";
        BindArtifact(command, artifact);
        ExecuteGuardingUnique(command, artifact.Kind, artifact.Name);
    }

    public void Update(FrameworkArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        GuardUniqueName(artifact.Kind, artifact.Name, exceptId: artifact.Id);

        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE framework_artifacts SET kind = $kind, name = $name, payload_json = $payload, " +
            "created_utc = $created, updated_utc = $updated WHERE id = $id;";
        BindArtifact(command, artifact);
        ExecuteGuardingUnique(command, artifact.Kind, artifact.Name);
    }

    public void Remove(string id)
    {
        using var connection = factory.Open();
        using var deleteResources = connection.CreateCommand();
        deleteResources.CommandText = "DELETE FROM framework_resources WHERE artifact_id = $id;";
        deleteResources.Parameters.AddWithValue("$id", id);
        deleteResources.ExecuteNonQuery();

        using var deleteArtifact = connection.CreateCommand();
        deleteArtifact.CommandText = "DELETE FROM framework_artifacts WHERE id = $id;";
        deleteArtifact.Parameters.AddWithValue("$id", id);
        deleteArtifact.ExecuteNonQuery();
    }

    public IReadOnlyList<FrameworkResource> ListResources(string artifactId)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {ResourceColumns} FROM framework_resources WHERE artifact_id = $id ORDER BY created_utc, file_name;";
        command.Parameters.AddWithValue("$id", artifactId);
        return ReadResources(command);
    }

    public FrameworkResource? GetResource(string resourceId)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ResourceColumns} FROM framework_resources WHERE id = $id;";
        command.Parameters.AddWithValue("$id", resourceId);
        var rows = ReadResources(command);
        return rows.Count > 0 ? rows[0] : null;
    }

    public void AddResource(FrameworkResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (resource.SizeBytes > FrameworkArtifactLimits.MaxResourceBytes)
        {
            throw new ResourceTooLargeException(resource.FileName, resource.SizeBytes);
        }

        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO framework_resources (id, artifact_id, file_name, content_type, bytes, size_bytes, created_utc) " +
            "VALUES ($id, $artifact, $file, $type, $bytes, $size, $created);";
        command.Parameters.AddWithValue("$id", resource.Id);
        command.Parameters.AddWithValue("$artifact", resource.ArtifactId);
        command.Parameters.AddWithValue("$file", resource.FileName);
        command.Parameters.AddWithValue("$type", (object?)resource.ContentType ?? DBNull.Value);
        command.Parameters.AddWithValue("$bytes", resource.Content);
        command.Parameters.AddWithValue("$size", resource.SizeBytes);
        command.Parameters.AddWithValue("$created", resource.CreatedUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void RemoveResource(string resourceId)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM framework_resources WHERE id = $id;";
        command.Parameters.AddWithValue("$id", resourceId);
        command.ExecuteNonQuery();
    }

    private void GuardUniqueName(FrameworkArtifactKind kind, string name, string? exceptId)
    {
        var existing = GetByName(kind, name);
        if (existing is not null && existing.Id != exceptId)
        {
            throw new DuplicateArtifactNameException(kind, name);
        }
    }

    private static void ExecuteGuardingUnique(SqliteCommand command, FrameworkArtifactKind kind, string name)
    {
        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // Backstop for the race the pre-check cannot cover (single-user, but keep the invariant honest).
            throw new DuplicateArtifactNameException(kind, name);
        }
    }

    private static void BindArtifact(SqliteCommand command, FrameworkArtifact artifact)
    {
        command.Parameters.AddWithValue("$id", artifact.Id);
        command.Parameters.AddWithValue("$kind", artifact.Kind.ToString());
        command.Parameters.AddWithValue("$name", artifact.Name);
        command.Parameters.AddWithValue("$payload", artifact.PayloadJson);
        command.Parameters.AddWithValue("$created", artifact.CreatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated", artifact.UpdatedUtc.ToString("O"));
    }

    private static List<FrameworkArtifact> ReadArtifacts(SqliteCommand command)
    {
        var result = new List<FrameworkArtifact>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new FrameworkArtifact(
                Id: reader.GetString(0),
                Kind: Enum.Parse<FrameworkArtifactKind>(reader.GetString(1)),
                Name: reader.GetString(2),
                PayloadJson: reader.GetString(3),
                CreatedUtc: ParseUtc(reader.GetString(4)),
                UpdatedUtc: ParseUtc(reader.GetString(5))));
        }

        return result;
    }

    private static List<FrameworkResource> ReadResources(SqliteCommand command)
    {
        var result = new List<FrameworkResource>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var bytes = (byte[])reader.GetValue(4);
            result.Add(new FrameworkResource(
                Id: reader.GetString(0),
                ArtifactId: reader.GetString(1),
                FileName: reader.GetString(2),
                ContentType: reader.IsDBNull(3) ? null : reader.GetString(3),
                Content: bytes,
                CreatedUtc: ParseUtc(reader.GetString(5))));
        }

        return result;
    }

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);
}
