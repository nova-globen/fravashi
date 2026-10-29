namespace Fravashi.Application.Verification;

/// <summary>What a design check does with one request when it carries a <see cref="WindowMarker"/>.</summary>
public enum WindowMarkerRoute
{
    /// <summary>Not a network request (<c>data:</c>, <c>blob:</c>): nothing is sent anywhere.</summary>
    Local,

    /// <summary>The run's own origin: the request goes, with the marker, and any redirect comes back to be decided again.</summary>
    Carry,

    /// <summary>Anything else: refused, so the marker never reaches it.</summary>
    Refuse,
}

/// <summary>
/// The window marker for ONE disposable studio run (backlog 226, ADR 0030 decision 8, spike 78 section 7, plan @69):
/// <c>FravashiWindow/1.0 tk/&lt;token&gt;</c>, where the token is the one the app runner minted for that run and handed
/// back to the verifier that started it. It is never the live studio's token. It belongs to the run's ORIGIN (scheme,
/// host and port) and to nothing else: a design check sends it to that origin only, and refuses every other request
/// from the context that carries it.
/// <para>
/// The token is private and <see cref="ToString"/> withholds it, so a record that holds a marker can be logged or
/// interpolated without printing it.
/// </para>
/// </summary>
public sealed class WindowMarker
{
    /// <summary>The product token the studio window adds to its user agent (ADR 0017).</summary>
    public const string Product = "FravashiWindow/1.0";

    /// <summary>What a withheld token is replaced with in text that is kept.</summary>
    public const string Withheld = "(window token withheld)";

    private readonly string _token;

    private WindowMarker(Uri origin, string token)
    {
        Origin = origin;
        _token = token;
    }

    /// <summary>The run's origin, the only place the marker goes.</summary>
    public Uri Origin { get; }

    /// <summary>A marker for the run at <paramref name="runUrl"/>, or null when either is missing or the address is not http(s).</summary>
    public static WindowMarker? For(string? runUrl, string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace)
            || !Uri.TryCreate(runUrl?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        return new WindowMarker(new Uri(uri.GetLeftPart(UriPartial.Authority) + "/"), token);
    }

    /// <summary>The user agent of a context that carries the marker: <paramref name="baseAgent"/> plus the marker and token.</summary>
    public string UserAgent(string baseAgent) => $"{baseAgent} {Product} tk/{_token}".TrimStart();

    /// <summary>Whether <paramref name="url"/> is the run's own origin; a WebSocket address counts as its http(s) twin.</summary>
    public bool Covers(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var scheme = uri.Scheme switch
        {
            "ws" => "http",
            "wss" => "https",
            _ => uri.Scheme,
        };
        return scheme == Origin.Scheme
            && uri.Host.Equals(Origin.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == Origin.Port;
    }

    /// <summary>What a context that carries the marker does with a request to <paramref name="url"/>.</summary>
    public WindowMarkerRoute Route(string? url)
    {
        if (url is not null
            && (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)))
        {
            return WindowMarkerRoute.Local;
        }

        return Covers(url) ? WindowMarkerRoute.Carry : WindowMarkerRoute.Refuse;
    }

    /// <summary>Why a request was refused, as the check's console records it.</summary>
    public string Refusal => $"the window marker goes only to the run's own address, {Origin.GetLeftPart(UriPartial.Authority)}";

    /// <summary><paramref name="text"/> with the token replaced, for anything that is kept (console lines, evidence).</summary>
    public string Withhold(string text) =>
        string.IsNullOrEmpty(text) ? text : text.Replace(_token, Withheld, StringComparison.Ordinal);

    public override string ToString() => $"the window marker for {Origin.GetLeftPart(UriPartial.Authority)} (its token withheld)";
}
