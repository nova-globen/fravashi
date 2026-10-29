namespace Fravashi.Launcher;

/// <summary>
/// The housekeeping after a container is recreated from a newer image (a reinstall or a product update): the image the
/// old container ran is removed, so an install does not keep one orphaned image per update (the 2026-07-28 disk-full
/// incident). Only when NOTHING names it any more (backlog 165).
/// <para>
/// The removal is by id and non-forced, and it used to rest on the belief that the engine refuses while another tag
/// references the image. Measured on podman 2026-09-29: it refuses with two names and removes with ONE, and by the time
/// the launcher retires the image <c>latest</c> has moved, so a single backup tag somebody put there on purpose was
/// its only name and went with it. Both release tools' printed back-outs had never worked for that reason. So the
/// launcher asks what still names the image first and keeps a named one, saying which name kept it.
/// </para>
/// </summary>
public static class SupersededImage
{
    /// <summary>
    /// Removes <paramref name="imageId"/> when nothing names it, otherwise keeps it and says why. A runtime that cannot
    /// say what names an image is treated as naming nothing, which is the previous behaviour and still non-forced.
    /// </summary>
    /// <param name="shown">How the image is written in the log line.</param>
    public static void Retire(IContainerRuntime runtime, string imageId, Action<string> log, string shown)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(log);

        var names = runtime.ImageNames(imageId);
        if (names.Count > 0)
        {
            log($"Kept the superseded image {shown}: it is still named {string.Join(", ", names)}.");
            return;
        }

        log($"Retiring the superseded image {shown}.");
        runtime.RemoveImage(imageId);
    }
}
