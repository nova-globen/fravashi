using System.Runtime.InteropServices;
using Fravashi.Domain;

namespace Fravashi.Launcher.Releases;

/// <summary>Maps the host CPU to the manifest's arch token (<c>amd64</c> / <c>arm64</c>, ADR 0009).</summary>
public static class HostArch
{
    public static string Current => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X64 => "amd64",
        var other => other.ToString().ToLowerInvariant(),
    };
}

/// <summary>
/// The <c>fravashi</c> product self-update (Type B, ADR 0006 / D03-7): a container cannot replace its own image, so
/// the launcher owns this. <see cref="EvaluateAsync"/> fetches the manifest (verified against the release key by the
/// source before it is handed over, backlog 242), runs the pure
/// <see cref="UpdateCheck"/>, and writes the outcome to <c>update-state.json</c> for the app's critical lock.
/// <see cref="ApplyAsync"/> downloads the new image, verifies its sha256 <b>before</b> loading, swaps the
/// container on the same reserved port + <c>/data</c> volume, bumps <c>launcher.json</c>'s version, and clears
/// the state. The DB is migrated by the app on open (bootstrap-on-open is idempotent, V6 "never lose the DB").
/// All side effects are injected, so the flow is unit-tested with a fake manifest, downloader, and runtime - no
/// network in committed tests.
/// </summary>
public sealed class ProductUpdater
{
    private readonly IReleaseManifestSource _manifests;
    private readonly IContainerRuntime _runtime;
    private readonly IReleaseAssetDownloader _downloader;
    private readonly LauncherConfigStore _configStore;
    private readonly StudioContainer _container;
    private readonly Action<string> _log;
    private readonly string _arch;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>How many times in all one slice (or a single asset) is tried on a failure that may pass (backlog 230).</summary>
    public const int DownloadAttempts = 3;

    /// <summary>The wait before the first retry; it doubles for each one after.</summary>
    public static readonly TimeSpan FirstRetryWait = TimeSpan.FromSeconds(2);

    /// <param name="container">
    /// The one place a studio container is created and what it baked is recorded (backlog 160). Required rather
    /// than defaulted: it carries whether this launcher holds a host bridge and an app runner, and an update
    /// that quietly assumed "none" is the defect this parameter exists to make impossible.
    /// </param>
    /// <param name="clock">The clock a check is recorded on.</param>
    /// <param name="delay">How a retry waits; tests move a fake clock instead of waiting.</param>
    public ProductUpdater(
        IReleaseManifestSource manifests,
        IContainerRuntime runtime,
        IReleaseAssetDownloader downloader,
        LauncherConfigStore configStore,
        StudioContainer container,
        Action<string>? log = null,
        string? arch = null,
        TimeProvider? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _manifests = manifests;
        _runtime = runtime;
        _downloader = downloader;
        _configStore = configStore;
        _container = container;
        _log = log ?? Console.WriteLine;
        _arch = arch ?? HostArch.Current;
        _clock = clock ?? TimeProvider.System;
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, _clock, ct));
    }

    /// <summary>
    /// Fetches + evaluates the manifest for this install. A manifest that was READ is evaluated and written to
    /// <c>update-state.json</c> (<see cref="UpdateAvailability.None"/> when nothing is newer), and any record of failed
    /// checks is cleared. A channel that could not be checked answers <see cref="UpdateKind.Unknown"/>, records the
    /// failure in <c>update-check.json</c>, and leaves <c>update-state.json</c> exactly as it was (backlog 230: writing
    /// "None" there lifted a critical lock on a network blip). This runs on every <c>fravashi</c> launch/status.
    /// </summary>
    public async Task<UpdateAvailability> EvaluateAsync(LauncherConfig config, CancellationToken cancellationToken = default)
    {
        var current = InstalledVersion(config);
        // When the check BEGAN is when it could not be made: a call that hung for its whole timeout failed at its start.
        var now = _clock.GetUtcNow();
        var fetch = await _manifests.FetchAsync(cancellationToken);
        var states = new UpdateStateStore(config.DataDir, _clock);
        var failures = new UpdateCheckFailureStore(config.DataDir);

        if (fetch.Manifest is not { } manifest)
        {
            var reason = fetch.CouldNotCheck ?? "no manifest was given";
            UpdateCheckFailure failure;
            try
            {
                failure = failures.Record(reason, now, fetch.Signature);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log($"Update check: could not record the failed check ({ex.Message}).");
                failure = new UpdateCheckFailure(reason, now, now) { Signature = fetch.Signature };
            }

            // Backlog 242: a manifest refused for its signature takes this same path, so nothing in it (a version, an
            // image, a critical flag) reaches the state the app's lock reads, in either direction.
            _log(fetch.Signature is null
                ? $"Update check: could not check the update channel ({reason}); the recorded update state is kept."
                : $"Update check: {reason}; the recorded update state is kept.");
            return UpdateAvailability.Unknown(failure with { Recorded = states.TryRead() });
        }

        var availability = UpdateCheck.Evaluate(manifest, current, InstalledVariant(config), _arch)
            with { Signature = ManifestSignature.Verified };

        try
        {
            states.Write(availability, current);
            failures.Clear();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"Update check: could not record the update state ({ex.Message}).");
        }

        return availability;
    }

    /// <summary>
    /// Applies an already-evaluated update: download → verify sha256 → load → swap container → bump launcher.json
    /// → clear state. Returns true on success (or when there was nothing to do). Safe to interrupt: nothing is
    /// swapped until the download verifies, so a failed pull leaves the current version running.
    /// </summary>
    public async Task<bool> ApplyAsync(LauncherConfig config, UpdateAvailability availability, CancellationToken cancellationToken = default)
    {
        if (availability.Kind == UpdateKind.Unknown)
        {
            // Backlog 230: nothing to install without a manifest, and not "up to date" either.
            _log($"The update channel could not be checked ({availability.CouldNotCheck?.Reason}); nothing was installed and your current version is unchanged.");
            return false;
        }

        if (availability.Kind == UpdateKind.None || availability.Image is null)
        {
            _log($"{ProductName.Short} is already up to date.");
            return true;
        }

        var image = availability.Image;
        if (string.IsNullOrWhiteSpace(image.AssetUrl) && image.PartUrls.Count == 0)
        {
            _log("The release manifest has no download URL for this host's image; cannot update.");
            return false;
        }

        var tarName = string.IsNullOrWhiteSpace(image.Asset) ? "fravashi-update.tar" : image.Asset;
        var tmp = Path.Combine(Path.GetTempPath(), tarName);

        _log($"Downloading {tarName} ({image.SizeBytes:N0} bytes)...");
        var download = image.PartUrls.Count > 0
            ? await DownloadSlicesAsync(image.PartUrls, tmp, cancellationToken)
            : await DownloadWithRetriesAsync(image.AssetUrl!, tmp, "The image", cancellationToken);
        if (download.Path is not { } downloaded)
        {
            _log($"The update download failed ({download.Failure}); your current version is unchanged.");
            return false;
        }

        if (!FileIntegrity.VerifyFile(downloaded, image.Sha256))
        {
            _log("The downloaded image failed its checksum check; refusing to install it. Your current version is unchanged.");
            TryDelete(downloaded);
            return false;
        }

        // The image id currently under the tag: once the load moves the tag and the container is swapped,
        // this image is superseded and is retired below (housekeeping: no orphaned image per update).
        var supersededImageId = _runtime.ResolveImageDigest(config.Image);

        _log("Loading the new image...");
        var loadedNames = _runtime.LoadImage(downloaded);

        var newImageRef = string.IsNullOrWhiteSpace(image.ImageRef) ? config.Image : image.ImageRef!;
        var runningImageId = _runtime.ContainerExists(config.ContainerName) ? _runtime.ContainerImageId(config.ContainerName) : null;

        // Backlog 256: what the image SAYS it is is decided from the name the load gave it, before the install's name
        // is moved onto it, so on wslc and Docker a refused image never gets that name at all. (On podman the load
        // has already moved it; RestoreInstallName below puts it back.)
        var found = LoadedImageName.Find(_runtime, loadedNames, newImageRef);
        if (found is not null
            && RefuseLabels(found.Source, availability.TargetVersion, InstalledVariant(config)) is { } labelRefusal)
        {
            return Refuse(labelRefusal, config, supersededImageId, runningImageId, found, downloaded);
        }

        // Backlog 190: the tarball names its image localhost/fravashi:latest, which on wslc and Docker is not the
        // name the container is created from. Bridge it, then check BEFORE the running container is touched that the
        // name really moved: creating from the old image while recording the new version stopped the studio on its
        // own integrity check, after a log line saying the update had succeeded.
        var loadedImageId = found is null ? null : LoadedImageName.Point(_runtime, found, newImageRef, _log);
        var newImageId = _runtime.ResolveImageDigest(newImageRef);
        if (RefuseUnmovedImage(newImageRef, newImageId, loadedImageId, runningImageId, supersededImageId) is { } refusal)
        {
            return Refuse(refusal, config, supersededImageId, runningImageId, refused: null, downloaded);
        }

        // The backstop for a load whose name could not be found: its labels are read from the install's name, which
        // is then already on it, so a refusal here is followed by putting the name back.
        if (found is null && RefuseLabels(newImageRef, availability.TargetVersion, InstalledVariant(config)) is { } lateRefusal)
        {
            return Refuse(lateRefusal, config, supersededImageId, runningImageId, refused: null, downloaded);
        }

        // Swap: stop + remove the old container so a fresh one is created from the new image on the same
        // reserved port and /data volume (the DB is on the volume and is migrated by the app on open).
        if (_runtime.ContainerExists(config.ContainerName))
        {
            _runtime.StopAndConfirm(config.ContainerName, _log);
            _runtime.RemoveContainer(config.ContainerName);
        }

        // F3: re-pin the digest and re-provision the expected-shape record for the swapped image, so the app's
        // integrity service verifies the NEW image (a stale pin from the old image would otherwise refuse the next
        // `fravashi` start). The new container is created WITH the integrity env so it is enforced immediately. The pin
        // is the id the check above confirmed.
        var updated = config with
        {
            Image = newImageRef,
            Version = availability.TargetVersion,
            Variant = InstalledVariant(config),
            PinnedImageDigest = newImageId,
        };

        var integrity = ProvisionIntegrity(updated);

        // Backlog 160: the container is created by the ONE place that creates them and records what it baked, so
        // the updated studio carries exactly what a start would give it (the window token of ADR 0017 and backlog
        // item 1, the host bridge, the app runner, and the VPN netns its kill switch depends on), and launcher.json
        // records those rather than the previous container's. Building a second spec here is what left an updated
        // studio with no bridge and nothing looking stale on the next start.
        updated = _container.Create(updated, integrity.KeyBase64, integrity.Digest);

        RetireSupersededImage(supersededImageId, updated.PinnedImageDigest);

        try
        {
            new UpdateStateStore(config.DataDir, _clock).Write(UpdateAvailability.None, updated.Version ?? InstalledVersion(config));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The app re-reads state; a stale critical flag is corrected on the next launch/status.
            _log($"Note: could not clear the update state ({ex.Message}).");
        }

        TryDelete(downloaded);
        _log($"Updated to {availability.TargetVersion}. {ProductName.Short} is running the new version on port {config.Port}.");
        return true;
    }

    /// <summary>
    /// Downloads a sliced asset (backlog 157) and joins the slices, in the manifest's order, into
    /// <paramref name="destination"/>. Each slice is tried up to <see cref="DownloadAttempts"/> times on a failure
    /// that may pass, and the slices already joined are kept while a later one is retried (backlog 230). When a slice
    /// still fails, nothing is left behind: a half-joined image must not survive to be mistaken for a whole one. The
    /// join is NOT verified here; the caller hashes the whole file, which is what the manifest's hash describes.
    /// </summary>
    private async Task<AssetDownload> DownloadSlicesAsync(IReadOnlyList<string> urls, string destination, CancellationToken cancellationToken)
    {
        var slices = destination + ".slices";
        var joined = false;
        try
        {
            Directory.CreateDirectory(slices);
            await using (var whole = File.Create(destination))
            {
                for (var i = 0; i < urls.Count; i++)
                {
                    _log($"Downloading part {i + 1} of {urls.Count}...");
                    var slice = await DownloadWithRetriesAsync(
                        urls[i], Path.Combine(slices, $"{i:D3}"), $"Part {i + 1} of {urls.Count}", cancellationToken);
                    if (slice.Path is not { } path)
                    {
                        return AssetDownload.Failed($"part {i + 1} of {urls.Count}: {slice.Failure}", slice.Retryable);
                    }

                    await using (var source = File.OpenRead(path))
                    {
                        await source.CopyToAsync(whole, cancellationToken);
                    }

                    TryDelete(path);
                }
            }

            joined = true;
            return AssetDownload.Done(destination);
        }
        finally
        {
            if (!joined)
            {
                TryDelete(destination);
            }

            try
            {
                Directory.Delete(slices, recursive: true);
            }
            catch (IOException)
            {
                // A leftover empty folder in temp is harmless.
            }
        }
    }

    /// <summary>
    /// Downloads one file, trying it again after <see cref="FirstRetryWait"/> (doubling) while the failure may pass,
    /// up to <see cref="DownloadAttempts"/> tries in all (backlog 230). A refusal is not retried. A failure leaves
    /// nothing at <paramref name="destination"/>.
    /// </summary>
    private async Task<AssetDownload> DownloadWithRetriesAsync(string url, string destination, string what, CancellationToken cancellationToken)
    {
        var wait = FirstRetryWait;
        for (var attempt = 1; ; attempt++)
        {
            var result = await _downloader.DownloadAsync(url, destination, cancellationToken);
            if (result.Path is not null)
            {
                return result;
            }

            TryDelete(destination);
            if (!result.Retryable || attempt >= DownloadAttempts || cancellationToken.IsCancellationRequested)
            {
                return result;
            }

            _log($"{what} did not arrive whole ({result.Failure}); trying again in {wait.TotalSeconds:0} s (attempt {attempt + 1} of {DownloadAttempts}).");
            await _delay(wait, cancellationToken);
            wait += wait;
        }
    }

    /// <summary>
    /// Writes the signed expected-shape record for the swapped image and returns the run-spec integrity env fields
    /// (F3). Best-effort: a failure downgrades the new container to report-only rather than aborting the update.
    /// </summary>
    private (string? KeyBase64, string? Digest) ProvisionIntegrity(LauncherConfig config)
    {
        var digest = config.PinnedImageDigest;
        if (string.IsNullOrWhiteSpace(digest))
        {
            return (null, null);
        }

        try
        {
            var key = new Integrity.IntegrityKeyStore(_configStore.ConfigDirectory).LoadOrCreate();
            var record = Integrity.IntegrityProvisioner.BuildRecord(config, _runtime.Name, digest);
            new Integrity.IntegrityRecordStore(config.DataDir).Write(record, key);
            return (Convert.ToBase64String(key), digest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"Integrity: could not record the expected shape after update ({ex.Message}); running report-only.");
            return (null, null);
        }
    }

    /// <summary>
    /// Housekeeping after a successful swap: removes the image the update replaced, so an install does not
    /// accumulate one orphaned <c>&lt;none&gt;</c> image per update. Skipped when the old id is unknown or
    /// not provably different from the new one, and kept when anything still names it (backlog 165: a remove by
    /// id takes an image's last tag with it, so a backup somebody tagged was deleted on every update).
    /// </summary>
    private void RetireSupersededImage(string? oldImageId, string? newImageDigest)
    {
        if (string.IsNullOrWhiteSpace(oldImageId) || string.IsNullOrWhiteSpace(newImageDigest)
            || string.Equals(TrimDigest(oldImageId), TrimDigest(newImageDigest), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SupersededImage.Retire(_runtime, oldImageId, _log, oldImageId);
    }

    private static string TrimDigest(string digest) =>
        digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest["sha256:".Length..] : digest;

    /// <summary>
    /// Why the container must NOT be swapped onto <paramref name="imageRef"/>, or null when it may (backlog 190). Each
    /// reason is one way the name the container is created from can fail to name what was just downloaded and
    /// verified. An id or label the engine cannot report is not evidence either way and refuses nothing.
    /// </summary>
    private static string? RefuseUnmovedImage(string imageRef, string? newId, string? loadedId, string? runningId, string? previousId)
    {
        if (string.IsNullOrWhiteSpace(newId))
        {
            return $"The new image was loaded, but {imageRef} names no image afterwards, so there is nothing to create the studio from.";
        }

        if (loadedId is not null && !LoadedImageName.SameId(newId, loadedId))
        {
            return $"The new image was loaded as {Short(loadedId)}, but {imageRef} still names {Short(newId)}.";
        }

        if ((runningId is not null && LoadedImageName.SameId(newId, runningId))
            || (previousId is not null && LoadedImageName.SameId(newId, previousId)))
        {
            return $"The new image was loaded, but {imageRef} still names the image the studio runs now ({Short(newId)}).";
        }

        return null;
    }

    /// <summary>
    /// Why the image <paramref name="reference"/> names is not the release it was offered as, read from its labels, or
    /// null when it may be installed. A label the engine cannot read, or an image without it (older builds), refuses
    /// nothing. The variant is refused too (backlog 256): the next start adopts an image's variant (backlog 251), so a
    /// wrong one would silently turn, say, a corp-broker install into a full one.
    /// </summary>
    private string? RefuseLabels(string reference, string? targetVersion, string installedVariant)
    {
        var labelled = _runtime.ImageReleaseVersion(reference);
        if (!string.IsNullOrWhiteSpace(labelled) && !string.IsNullOrWhiteSpace(targetVersion)
            && !string.Equals(labelled.Trim().TrimStart('v'), targetVersion.Trim().TrimStart('v'), StringComparison.OrdinalIgnoreCase))
        {
            return $"{reference} names an image that says it is version {labelled.Trim()}, not {targetVersion}.";
        }

        var variant = _runtime.ImageVariant(reference);
        if (!string.IsNullOrWhiteSpace(variant)
            && !string.Equals(variant.Trim(), installedVariant.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return $"{reference} names an image that says it is the {variant.Trim()} variant, not {installedVariant}.";
        }

        return null;
    }

    /// <summary>
    /// Ends a refused update (backlog 256): the install's name is put back on the image it named before the load, the
    /// image refused for what it says it is is retired once that holds, and the log says the studio is unchanged only
    /// when the name really does name what it did, because that name is what the next start creates the studio from.
    /// </summary>
    /// <param name="refused">The loaded image when it was refused for its labels; null for every other refusal.</param>
    private bool Refuse(
        string refusal, LauncherConfig config, string? previousId, string? runningId, LoadedImage? refused, string downloaded)
    {
        if (RestoreInstallName(config.Image, previousId) is { } stillMoved)
        {
            _log($"{refusal} The studio was not swapped, but {stillMoved}");
        }
        else
        {
            if (refused is not null)
            {
                RetireRefusedImage(refused, config.Image, previousId, runningId);
            }

            _log($"{refusal} Nothing was swapped: the studio you have keeps running at version {InstalledVersion(config)}.");
        }

        TryDelete(downloaded);
        return false;
    }

    /// <summary>
    /// Makes <paramref name="name"/> resolve to <paramref name="previousId"/> again when the load moved it (podman's load
    /// replaces the name itself). Null when the name names what it named before, else the rest of a sentence saying what
    /// it names now and what the next start would do.
    /// </summary>
    private string? RestoreInstallName(string name, string? previousId)
    {
        var now = _runtime.ResolveImageDigest(name);
        if (string.IsNullOrWhiteSpace(previousId))
        {
            return now is null
                ? null
                : $"{name} named no image before the update and now names {Short(now)}, the image that was just refused. "
                    + $"The next `{ProductName.Command}` start would create the studio from it.";
        }

        if (now is not null && LoadedImageName.SameId(now, previousId))
        {
            return null;
        }

        if (_runtime.TagImage(previousId, name)
            && _runtime.ResolveImageDigest(name) is { } after && LoadedImageName.SameId(after, previousId))
        {
            _log($"The load had moved {name} onto {(now is null ? "nothing" : Short(now))}; named the previous image {Short(previousId)} {name} again.");
            return null;
        }

        var names = now is null ? "no image" : Short(now);
        return $"{name} could not be named back to the image it named before the update ({Short(previousId)}): it names {names}. "
            + $"The next `{ProductName.Command}` start would use that; name it back with `{_runtime.Name} tag {previousId} {name}` before starting {ProductName.Short} again.";
    }

    /// <summary>
    /// Removes an image refused for what it says it is, once the install's name is off it. The name the load gave it goes
    /// first when it still names it (on wslc and Docker that is the tarball's own name; on podman it is the install's name,
    /// back on the previous image, so it is left alone), then the id the way a superseded image goes: kept, and said,
    /// when anything else still names it. The previous and the running image are never touched.
    /// </summary>
    private void RetireRefusedImage(LoadedImage refused, string installName, string? previousId, string? runningId)
    {
        if ((previousId is not null && LoadedImageName.SameId(refused.Id, previousId))
            || (runningId is not null && LoadedImageName.SameId(refused.Id, runningId))
            || (_runtime.ResolveImageDigest(installName) is { } install && LoadedImageName.SameId(install, refused.Id)))
        {
            return;
        }

        if (_runtime.ResolveImageDigest(refused.Source) is { } byLoadName && LoadedImageName.SameId(byLoadName, refused.Id))
        {
            _runtime.RemoveImage(refused.Source);
        }

        var names = _runtime.ImageNames(refused.Id);
        if (names.Count > 0)
        {
            _log($"Kept the refused image {Short(refused.Id)}: it is still named {string.Join(", ", names)}.");
            return;
        }

        _log($"Removed the refused image {Short(refused.Id)}.");
        _runtime.RemoveImage(refused.Id);
    }

    private static string Short(string digest)
    {
        var hex = TrimDigest(digest);
        return $"sha256:{hex[..Math.Min(12, hex.Length)]}";
    }

    private static string InstalledVersion(LauncherConfig config) =>
        string.IsNullOrWhiteSpace(config.Version) ? "0.0.0" : config.Version!;

    private static string InstalledVariant(LauncherConfig config) =>
        string.IsNullOrWhiteSpace(config.Variant) ? LauncherDefaults.DefaultVariant : config.Variant!;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover temp tar is harmless.
        }
    }
}
