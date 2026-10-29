using System.Security.Cryptography;

namespace Fravashi.Launcher.Integrity;

/// <summary>
/// Holds the launcher's HMAC key that signs the integrity record (F3 / ADR 0007). The key is generated once and
/// persisted host-side next to <c>launcher.json</c> - deliberately NOT in the mounted <c>/data</c> directory,
/// because a key co-located with the record it signs would let anyone who can edit the record re-sign it, which
/// would make the HMAC worthless. The launcher injects the key into the container as the
/// <see cref="IntegrityEnv.KeyVar"/> env var so the app can verify the record; that env is part of the runtime
/// shape only the launcher creates.
/// <para>
/// The key is STABLE across launches on purpose: an existing container carries the key env from when it was
/// created, so re-signing every record with the same key keeps a plain restart verifying (a rotated key would
/// false-trip the running container). It is regenerated only when the file is absent.
/// </para>
/// </summary>
public sealed class IntegrityKeyStore
{
    /// <summary>The key file name, kept beside <c>launcher.json</c> in the config directory.</summary>
    public const string FileName = "integrity.key";

    private const int KeyBytes = 32;

    private readonly string _path;

    public IntegrityKeyStore(string configDirectory) =>
        _path = Path.Combine(configDirectory, FileName);

    /// <summary>The raw key bytes, loading an existing key or generating + persisting a new one.</summary>
    public byte[] LoadOrCreate()
    {
        if (File.Exists(_path))
        {
            try
            {
                var existing = Convert.FromBase64String(File.ReadAllText(_path).Trim());
                if (existing.Length >= 16)
                {
                    return existing;
                }
            }
            catch (FormatException)
            {
                // A corrupt key file is replaced below rather than blocking the launch.
            }
        }

        var key = RandomNumberGenerator.GetBytes(KeyBytes);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, Convert.ToBase64String(key));
        return key;
    }

    /// <summary>The base64 form injected into the container as <see cref="IntegrityEnv.KeyVar"/>.</summary>
    public string LoadOrCreateBase64() => Convert.ToBase64String(LoadOrCreate());
}
