using System.Globalization;
using System.Text;

namespace Fravashi.Application.Verification;

/// <summary>
/// Reads an observation written down as text (backlog 222, plan @46): the recorded fixtures, and anything else that
/// keeps a page's observation as a file. A line starting with <c>@</c> is a directive; every other line belongs to the
/// current phase's aria snapshot, verbatim.
/// <code>
/// @screen 0
/// @phase at rest
/// @viewport wide 1280
/// - heading "Team directory" [level=1] [ref=e3]
/// @box e3 32 24 1216 34
/// @contrast e3 14.23
/// @phase add-dialog opened by Add person
/// @viewport wide 1280
/// @not-reached button "Add person" (add-person) was not found
/// </code>
/// <c>@box</c> and <c>@contrast</c> name an element by its snapshot reference and must follow the snapshot that has it.
/// </summary>
public static class DesignObservationText
{
    /// <summary>The observation <paramref name="text"/> records.</summary>
    /// <exception cref="FormatException">A directive is malformed or names a reference its phase does not have.</exception>
    public static DesignObservation Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var screen = 0;
        var phases = new List<ObservedPhase>();
        Pending? current = null;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var number = i + 1;
            var line = lines[i];
            if (!line.StartsWith('@'))
            {
                if (line.Trim().Length == 0 && current is null)
                {
                    continue;
                }

                (current ?? throw Error(number, "a snapshot line before any @phase")).Snapshot(line, number);
                continue;
            }

            var space = line.IndexOf(' ', StringComparison.Ordinal);
            var directive = space < 0 ? line[1..] : line[1..space];
            var argument = space < 0 ? string.Empty : line[(space + 1)..].Trim();
            switch (directive)
            {
                case "screen":
                    screen = Integer(argument, number);
                    break;

                case "phase":
                    if (current is not null)
                    {
                        phases.Add(current.Finish());
                    }

                    current = new Pending(argument.Length > 0 ? argument : throw Error(number, "a phase with no name"));
                    break;

                case "viewport":
                    var parts = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var phase = current ?? throw Error(number, "a viewport before any @phase");
                    phase.Viewport = parts.Length == 2 ? parts[0] : throw Error(number, "a viewport that is not a name and a width");
                    phase.Width = Integer(parts[1], number);
                    break;

                case "not-reached":
                    (current ?? throw Error(number, "@not-reached before any @phase")).NotReached =
                        argument.Length > 0 ? argument : throw Error(number, "@not-reached with no reason");
                    break;

                case "box":
                    var box = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (box.Length != 5)
                    {
                        throw Error(number, "a box that is not a reference and four numbers");
                    }

                    (current ?? throw Error(number, "@box before any @phase")).Boxes[box[0]] = new ObservedBox(
                        Number(box[1], number), Number(box[2], number), Number(box[3], number), Number(box[4], number));
                    break;

                case "contrast":
                    var contrast = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (contrast.Length != 2)
                    {
                        throw Error(number, "a contrast that is not a reference and a number");
                    }

                    (current ?? throw Error(number, "@contrast before any @phase")).Contrasts[contrast[0]] = Number(contrast[1], number);
                    break;

                default:
                    throw Error(number, $"an unknown directive @{directive}");
            }
        }

        if (current is not null)
        {
            phases.Add(current.Finish());
        }

        return new DesignObservation(screen, phases);
    }

    private static int Integer(string text, int line) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw Error(line, $"\"{text}\" is not a whole number");

    private static double Number(string text, int line) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw Error(line, $"\"{text}\" is not a number");

    private static FormatException Error(int line, string what) =>
        new(string.Create(CultureInfo.InvariantCulture, $"Observation line {line}: {what}."));

    private sealed class Pending(string phase)
    {
        private readonly StringBuilder snapshot = new();
        private int firstLine;

        public string? Viewport { get; set; }
        public int? Width { get; set; }
        public string? NotReached { get; set; }
        public Dictionary<string, ObservedBox> Boxes { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, double> Contrasts { get; } = new(StringComparer.Ordinal);

        public void Snapshot(string line, int number)
        {
            if (snapshot.Length == 0)
            {
                firstLine = number;
            }

            snapshot.Append(line).Append('\n');
        }

        public ObservedPhase Finish()
        {
            if (NotReached is not null)
            {
                if (snapshot.ToString().Trim().Length > 0 || Boxes.Count > 0 || Contrasts.Count > 0)
                {
                    throw Error(firstLine, $"phase \"{phase}\" is not reached and also has a snapshot");
                }

                return new ObservedPhase(phase, Viewport, Width, null, NotReached);
            }

            var nodes = AriaSnapshot.Parse(snapshot.ToString(), firstLine);
            var used = new HashSet<string>(StringComparer.Ordinal);
            var probed = nodes.Select(n => Probe(n, used)).ToArray();
            var unknown = Boxes.Keys.Concat(Contrasts.Keys).FirstOrDefault(r => !used.Contains(r));
            if (unknown is not null)
            {
                throw Error(firstLine, $"phase \"{phase}\" measures {unknown}, which its snapshot does not have");
            }

            return new ObservedPhase(phase, Viewport, Width, probed, null);
        }

        private ObservedNode Probe(ObservedNode node, HashSet<string> used)
        {
            if (node.Ref is { } reference)
            {
                used.Add(reference);
            }

            return node with
            {
                Box = node.Ref is { } b && Boxes.TryGetValue(b, out var box) ? box : null,
                Contrast = node.Ref is { } c && Contrasts.TryGetValue(c, out var contrast) ? contrast : null,
                Children = node.Children.Select(child => Probe(child, used)).ToArray(),
            };
        }
    }
}
