using Fravashi.Application.AgentRuntimes;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.AgentRuntimes;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.AgentRuntimes;

public class AgentLoginTextParserTests
{
    [Fact]
    public void Extracts_a_url_trimming_trailing_punctuation()
    {
        AgentLoginTextParser.ExtractUrl("Please visit https://github.com/login/device.")
            .Should().Be("https://github.com/login/device");
    }

    [Fact]
    public void Extracts_a_device_code_not_a_url_fragment()
    {
        AgentLoginTextParser.ExtractDeviceCode("First copy your one-time code: WDJB-MJHT")
            .Should().Be("WDJB-MJHT");
        AgentLoginTextParser.ExtractDeviceCode("open https://github.com/login/device")
            .Should().BeNull();
    }

    [Fact]
    public void Strips_ansi_control_sequences()
    {
        AgentLoginTextParser.StripControl("[32mgreen[0m text\r").Should().Be("green text");
    }

    /// <summary>
    /// An OSC sequence, which is not the CSI kind the test above covers: it opens with ESC and a closing square
    /// bracket, and ends at a BEL or at ESC and a backslash. The pinned Copilot CLI's device-code sign-in sends
    /// one (OSC 52, "put this on the clipboard") carrying the one-time code in base64, measured in the container
    /// on 2026-09-21. Unstripped it is the first line of garbage a user sees while signing in. The control
    /// characters are spelled as code points so nothing can decode them on the way to disk.
    /// </summary>
    [Theory]
    [InlineData(0x07, "")]
    [InlineData(0x1B, "\\")]
    public void Strips_an_operating_system_command_sequence_whichever_way_it_ends(int terminator, string tail)
    {
        var esc = ((char)0x1B).ToString();
        var osc = esc + "]52;c;RTJCOC00N0FE" + (char)terminator + tail;

        AgentLoginTextParser.StripControl("Waiting for authorization..." + osc + osc).Should().Be("Waiting for authorization...");
        AgentLoginTextParser.StripControl(osc).Should().BeEmpty();
    }

    [Fact]
    public void ToChunk_marks_stderr_as_error_and_carries_highlights()
    {
        var chunk = AgentLoginTextParser.ToChunk(
            DateTimeOffset.UtcNow, "go to https://x.test and enter ABCD-1234", isError: true);
        chunk.Kind.Should().Be(AgentLoginChunkKind.Error);
        chunk.Url.Should().Be("https://x.test");
        chunk.DeviceCode.Should().Be("ABCD-1234");
    }
}

public class AgentLoginSessionTests
{
    [Fact]
    public async Task Streams_parsed_output_forwards_input_and_ends_with_a_notice()
    {
        var pipe = new FakeLoginPipe();
        await using var session = new AgentLoginSession(AgentTool.Claude, pipe);

        var chunks = new List<AgentLoginChunk>();
        var reader = Task.Run(async () =>
        {
            await foreach (var chunk in session.Output.ReadAllAsync())
            {
                chunks.Add(chunk);
            }
        });

        pipe.Emit("Open https://github.com/login/device and enter WDJB-MJHT");
        await session.SendInputAsync("hello");
        pipe.Finish(0);

        (await session.Completion).Should().Be(0);
        await reader;

        chunks.Should().Contain(c => c.Kind == AgentLoginChunkKind.Notice); // start + end notices
        chunks.Should().Contain(c => c.Url == "https://github.com/login/device" && c.DeviceCode == "WDJB-MJHT");
        pipe.Sent.Should().ContainSingle().Which.Should().Be("hello");
    }
}

/// <summary>
/// Backlog 183 (decided 2026-10-01): the sign-in makes a long-lived subscription token and PRINTS it. The studio
/// captures it, never shows it, and keeps it encrypted, so every conversation is handed the same one rather than a copy
/// of a refreshable sign-in that another conversation's refresh made stale. The tokens here are made up.
/// </summary>
public class SubscriptionTokenSignInTests
{
    private const string Token = "sk-" + "ant-oat01-AbCdEfGhIjKlMnOpQrStUvWxYz0123456789_-zyxwvutsrq";

    private static async Task<(List<AgentLoginChunk> Chunks, List<string> Kept)> SignInAsync(
        IEnumerable<string> lines, int exitCode = 0, string? refusal = null)
    {
        var pipe = new FakeLoginPipe();
        var kept = new List<string>();
        await using var session = new AgentLoginSession(AgentTool.Claude, pipe, token =>
        {
            kept.Add(token);
            return refusal;
        });

        var chunks = new List<AgentLoginChunk>();
        var reader = Task.Run(async () =>
        {
            await foreach (var chunk in session.Output.ReadAllAsync())
            {
                chunks.Add(chunk);
            }
        });

        foreach (var line in lines)
        {
            pipe.Emit(line);
        }

        pipe.Finish(exitCode);
        await session.Completion;
        await reader;
        return (chunks, kept);
    }

    [Fact]
    public async Task The_printed_token_is_kept_and_never_shown()
    {
        var (chunks, kept) = await SignInAsync(
        [
            "Browse to https://claude.ai/oauth/authorize?code=true and paste the code here:",
            "\u001b[32m✓ Long-lived authentication token created successfully!\u001b[0m",
            $"Your OAuth token (valid for 1 year): {Token}",
            "Store this token securely.",
        ]);

        kept.Should().Equal(Token);
        chunks.Should().NotContain(c => c.Text.Contains("sk-ant-oat", StringComparison.Ordinal));
        chunks.Should().Contain(c => c.Text.Contains(AgentLoginTextParser.RedactedToken, StringComparison.Ordinal));
        chunks.Should().Contain(c => c.Text.StartsWith("Signed in: the token is stored encrypted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_token_wrapped_after_its_body_began_is_kept_by_neither_half_and_shown_by_neither()
    {
        // Backlog 268: the head is already a whole token, so the next line could be its rest or a word like "Done", and
        // there is no telling without the token's length, which is not measured. Neither a cut token nor a lengthened one
        // is kept.
        var head = Token[..40];
        var tail = Token[40..];
        var (chunks, kept) = await SignInAsync([head, tail, "Store this token securely."]);

        kept.Should().BeEmpty();
        chunks.Should().NotContain(c => c.Text.Contains(tail, StringComparison.Ordinal));
        chunks.Should().Contain(c => c.Text == "Store this token securely.", "a line after the token is shown again");
    }

    [Fact]
    public async Task A_sign_in_that_printed_no_token_says_nothing_was_stored()
    {
        var (chunks, kept) = await SignInAsync(["Something went wrong on the way."]);

        kept.Should().BeEmpty();
        chunks.Should().Contain(c => c.Text.Contains("no token appeared", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_token_the_store_refused_says_why()
    {
        var (chunks, _) = await SignInAsync([Token], refusal: "the credential broker carries this runtime.");

        chunks.Should().Contain(c => c.Text == "The token was not stored: the credential broker carries this runtime.");
    }

    [Theory]
    [InlineData("the credential broker carries this runtime.")]
    [InlineData(null)]
    public async Task The_refusal_is_on_the_session_by_the_time_its_output_ends(string? refusal)
    {
        // Backlog 267: the panel reads this, not the notice text, to decide that a sign-in did not sign anything in.
        var pipe = new FakeLoginPipe();
        await using var session = new AgentLoginSession(AgentTool.Claude, pipe, _ => refusal);
        pipe.Emit(Token);
        pipe.Finish(0);

        await foreach (var _ in session.Output.ReadAllAsync())
        {
        }

        session.TokenRefusal.Should().Be(refusal);
    }

    [Fact]
    public async Task A_failed_sign_in_keeps_nothing()
    {
        var (_, kept) = await SignInAsync([Token], exitCode: 1);

        kept.Should().BeEmpty("a token is kept only from a sign-in that finished");
    }

    [Fact]
    public void Claude_signs_in_with_setup_token_and_is_handed_the_token_under_its_own_variable()
    {
        var claude = AgentRuntimeCatalog.For(AgentTool.Claude);

        claude.InteractiveLoginArguments.Should().Equal("setup-token");
        claude.SubscriptionTokenEnvVar.Should().Be("CLAUDE_CODE_OAUTH_TOKEN");
        AgentProcessEnvironment.IsAllowed("CLAUDE_CODE_OAUTH_TOKEN").Should().BeFalse(
            "a token reaches a child only when the credential store puts it there, never by inheritance");
        Fravashi.Application.Projects.ProjectVariableNames.Refusal("CLAUDE_CODE_OAUTH_TOKEN").Should().NotBeNull();
    }
}

/// <summary>
/// Backlog 268: on 2026-10-08 the panel SHOWED the token the pinned CLI printed and kept nothing. Why is not measured: the
/// real output holds a real token, so nobody has looked at it. Each case here is one way a terminal can break a token on
/// its way to the parser, built from a made-up token of the same shape, and the answer is always one of two: the whole
/// token is kept, or nothing is; and in neither case is any part of it shown. The control characters are spelled as code
/// points so nothing can decode them on the way to disk.
/// </summary>
public class TokenTheTerminalBrokeTests
{
    private const string Token = "sk-" + "ant-oat01-AbCdEfGhIjKlMnOpQrStUvWxYz0123456789_-zyxwvutsrq";
    private const string OtherToken = "sk-" + "ant-oat01-ZyXwVuTsRqPoNmLkJiHgFeDcBa9876543210-_qrstuvwxyz";

    private static string Esc => ((char)27).ToString();

    private static string Border => ((char)0x2502).ToString();

    private static readonly Dictionary<string, (string[] Lines, bool Kept)> Cases = new()
    {
        ["a colour inside the token"] = ([$"Your OAuth token: {Token[..20]}{Esc}[1m{Token[20..]}{Esc}[0m"], true),
        ["a colour in the colon form inside the token"] = ([$"Your OAuth token: {Token[..20]}{Esc}[38:5:208m{Token[20..]}{Esc}[0m"], true),
        ["a cursor-forward over a cell of the token"] = ([$"Your OAuth token: {Token[..20]}{Esc}[1C{Token[21..]}"], false),
        ["a column jump inside the token"] = ([$"Your OAuth token: {Token[..20]}{Esc}[40G{Token[20..]}"], false),
        ["a cursor restore inside the token"] = ([$"Your OAuth token: {Token[..20]}{Esc}8{Token[20..]}"], false),
        ["a cursor restore in the CSI form inside the token"] = ([$"Your OAuth token: {Token[..20]}{Esc}[u{Token[20..]}"], false),
        ["a column jump in the backtick form inside the token"] = ([$"Your OAuth token: {Token[..20]}{Esc}[40`{Token[20..]}"], false),
        ["a relative move right inside the token"] = ([$"Your OAuth token: {Token[..20]}{Esc}[1a{Token[21..]}"], false),
        ["a tab inside the token"] = ([$"Your OAuth token: {Token[..20]}{Esc}[I{Token[20..]}"], false),
        ["a tab character inside the token"] = ([$"Your OAuth token: {Token[..20]}{(char)0x09}{Token[20..]}"], false),
        ["a backspace inside the token"] = ([$"Your OAuth token: {Token[..20]}x{(char)0x08}{Token[20..]}"], false),
        ["a DEL inside the token"] = ([$"Your OAuth token: {Token[..20]}{(char)0x7F}{Token[20..]}"], false),
        ["a bare escape inside the token"] = ([$"Your OAuth token: {Token[..20]}{Esc}{Token[20..]}"], false),
        // A wrap after the body began leaves a head that is already whole, so it cannot be told from a following word; see
        // the "Done" case. Nothing is kept and neither half is shown.
        ["a wrap inside a box border"] =
            ([$"{Border} Your OAuth token: {Token[..30]} {Border}", $"{Border} {Token[30..]}                    {Border}", $"{Border} Store this token securely. {Border}"], false),
        ["a wrap with leading spaces"] = ([$"   {Token[..30]}", $"   {Token[30..]}", "Store this token securely."], false),
        ["a wrapped tail of two characters"] = ([Token[..^2], Token[^2..], "Store this token securely."], false),
        ["a wrap inside the prefix"] = ([$"Your OAuth token: {Token[..5]}", Token[5..], "Store this token securely."], true),
        ["a wrap inside the prefix inside a box border"] =
            ([$"{Border} Your OAuth token: {Token[..9]} {Border}", $"{Border} {Token[9..]} {Border}", $"{Border} Store this token securely. {Border}"], true),
        ["a word after a whole token"] = ([$"Your OAuth token: {Token}", "Done"], false),
        ["a row of hyphens after a whole token"] = ([$"Your OAuth token: {Token}", "-----", "Store this token securely."], true),
        ["CR line endings"] = ([$"Your OAuth token: {Token}\r", "\r", "Store this token securely.\r"], true),
        ["a hyperlink around the token that carries it too"] =
            ([$"Your OAuth token: {Esc}]8;;https://console.example.test/?t={Token}{Esc}\\{Token}{Esc}]8;;{Esc}\\"], true),
        ["a fragment whose head was lost"] = (["Your OAuth token:", Token[7..], "Store this token securely."], false),
        ["a fragment whose head was lost past oat"] = (["Your OAuth token:", Token[8..], "Store this token securely."], false),
        ["a fragment whose head was lost up to the hyphen"] = (["Your OAuth token:", Token[12..], "Store this token securely."], false),
        ["the bare body"] = (["Your OAuth token:", Token[13..], "Store this token securely."], false),
        ["the bare body after text on its line"] = ([$"Your OAuth token: {Token[13..]}", "Store this token securely."], false),
        // The CLI draws the gap between words with a cursor-forward (2026-10-02), so a move bounds a word as a space does.
        ["the bare body after a cursor-forward gap"] = ([$"Your OAuth token:{Esc}[1C{Token[13..]}", "Store this token securely."], false),
        ["a partial prefix with a cursor move after it"] =
            ([$"Your OAuth token: {Token[..5]}{Esc}[1C", Token[5..], "Store this token securely."], true),
        ["a prefix with no body"] = ([$"Your OAuth token: {Token[..7]}"], false),
        ["two different tokens"] = ([$"Your OAuth token: {Token}", $"Your OAuth token: {OtherToken}"], false),
    };

    public static TheoryData<string> CaseNames => new(Cases.Keys);

    private static async Task<(List<AgentLoginChunk> Chunks, List<string> Kept)> SignInAsync(string[] lines, int exitCode = 0)
    {
        var pipe = new FakeLoginPipe();
        var kept = new List<string>();
        await using var session = new AgentLoginSession(AgentTool.Claude, pipe, token =>
        {
            kept.Add(token);
            return null;
        });

        var chunks = new List<AgentLoginChunk>();
        var reader = Task.Run(async () =>
        {
            await foreach (var chunk in session.Output.ReadAllAsync())
            {
                chunks.Add(chunk);
            }
        });

        foreach (var line in lines)
        {
            pipe.Emit(line);
        }

        pipe.Finish(exitCode);
        await session.Completion;
        await reader;
        return (chunks, kept);
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task The_whole_token_is_kept_or_nothing_is(string name)
    {
        var (lines, keepsIt) = Cases[name];
        var (chunks, kept) = await SignInAsync(lines);

        if (keepsIt)
        {
            kept.Should().Equal([Token], name);
            chunks.Should().Contain(c => c.Text.StartsWith("Signed in: the token is stored encrypted", StringComparison.Ordinal));
        }
        else
        {
            kept.Should().BeEmpty(name);
            chunks.Should().Contain(c => c.Kind == AgentLoginChunkKind.Notice && c.Text.Contains("nothing was stored", StringComparison.Ordinal));
        }
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task No_shown_line_carries_any_part_of_the_token(string name)
    {
        var (lines, _) = Cases[name];
        var (chunks, _) = await SignInAsync(lines);

        foreach (var token in new[] { Token, OtherToken })
        {
            for (var at = 0; at + 8 <= token.Length; at++)
            {
                var window = token.Substring(at, 8);
                chunks.Should().NotContain(c => c.Text.Contains(window, StringComparison.Ordinal), $"{name}: \"{window}\" is part of the token");
            }
        }

        chunks.Should().NotContain(c => c.Text.Contains("sk-ant", StringComparison.Ordinal), name);
    }

    [Fact]
    public async Task A_token_shaped_value_that_could_not_be_read_whole_is_named_as_such()
    {
        var (chunks, _) = await SignInAsync([$"Your OAuth token: {Token[..20]}{Esc}[1C{Token[21..]}"]);

        chunks.Should().Contain(c => c.Kind == AgentLoginChunkKind.Notice && c.Text.Contains("could not be read whole", StringComparison.Ordinal));
    }

    [Fact]
    public void A_word_with_no_sign_is_hidden_from_the_floor_on_and_the_sign_in_link_never_is()
    {
        var body = Token[13..];
        var scanner = new SubscriptionTokenScanner();

        scanner.Show(body[..(SubscriptionTokenScanner.BodyFloor - 1)]).Should().Be(body[..(SubscriptionTokenScanner.BodyFloor - 1)]);
        scanner.Show(body[..SubscriptionTokenScanner.BodyFloor]).Should().Be(AgentLoginTextParser.RedactedToken);

        var link = "Browse to https://claude.ai/oauth/authorize?code=true&client_id=AbCdEfGhIjKlMnOpQrStUvWxYz0123456789&state=Zz";
        new SubscriptionTokenScanner().Show(link).Should().Be(link);
        new SubscriptionTokenScanner().Show("------------------------------").Should().Be("------------------------------");
    }

    [Fact]
    public async Task A_word_after_a_whole_token_is_never_joined_to_it()
    {
        var (chunks, kept) = await SignInAsync([$"Your OAuth token: {Token}", "Done"]);

        kept.Should().NotContain(Token + "Done");
        kept.Should().BeEmpty("the word could be the rest of a wrapped token, so the token is ambiguous");
        chunks.Should().Contain(c => c.Kind == AgentLoginChunkKind.Notice && c.Text.Contains("could not be read whole", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_row_of_hyphens_after_a_whole_token_ends_it_and_is_shown()
    {
        var (chunks, kept) = await SignInAsync([$"Your OAuth token: {Token}", "-----"]);

        kept.Should().Equal([Token]);
        chunks.Should().Contain(c => c.Text == "-----");
    }

    [Fact]
    public async Task Two_different_tokens_are_named_as_such()
    {
        var (chunks, _) = await SignInAsync([Token, OtherToken]);

        chunks.Should().Contain(c => c.Kind == AgentLoginChunkKind.Notice && c.Text.Contains("more than one different token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_sign_in_that_did_not_finish_says_nothing_was_stored_and_shows_nothing()
    {
        var (chunks, kept) = await SignInAsync([$"Your OAuth token: {Token}"], exitCode: 1);

        kept.Should().BeEmpty();
        chunks.Should().Contain(c => c.Kind == AgentLoginChunkKind.Notice && c.Text.Contains("nothing was stored", StringComparison.Ordinal));
        var part = Token[7..15];
        chunks.Should().NotContain(c => c.Text.Contains(part, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_sign_in_that_keeps_no_token_still_shows_none()
    {
        // A runtime with nowhere to keep a token is masked all the same.
        var pipe = new FakeLoginPipe();
        await using var session = new AgentLoginSession(AgentTool.Claude, pipe);
        pipe.Emit($"Your OAuth token: {Token[..5]}");
        pipe.Emit(Token[5..]);
        pipe.Finish(0);

        var chunks = new List<AgentLoginChunk>();
        await foreach (var chunk in session.Output.ReadAllAsync())
        {
            chunks.Add(chunk);
        }

        var part = Token[10..18];
        chunks.Should().NotContain(c => c.Text.Contains(part, StringComparison.Ordinal));
        chunks.Should().NotContain(c => c.Text.Contains("sk-an", StringComparison.Ordinal));
    }
}

/// <summary>
/// The sign-in the owner could not finish on 2026-10-02: the code they pasted was never submitted, the link was cut at 80
/// columns, and the text read "WelcometoClaudeCode" between stray escapes and spinner frames. Each was replayed in a
/// container against the pinned CLI before it was fixed.
/// </summary>
public class SignInTerminalTests
{
    private static string Esc => ((char)27).ToString();

    [Fact]
    public void Under_script_Enter_is_a_carriage_return_and_run_directly_it_is_a_newline()
    {
        ProcessLoginPipe.EnterFor("script").Should().Be(((char)13).ToString(), "a raw terminal submits on CR; an LF was echoed and ignored");
        ProcessLoginPipe.EnterFor("/usr/bin/script").Should().Be(((char)13).ToString());
        ProcessLoginPipe.EnterFor("claude").Should().Be(Environment.NewLine);
    }

    [Fact]
    public void The_terminal_the_sign_in_runs_in_is_wide_enough_for_its_link_and_its_token()
    {
        var wrapped = ProcessAgentLoginProxy.BuildCommand("claude setup-token", pseudoTty: true);
        wrapped.FileName.Should().Be("script");
        wrapped.Arguments.Should().Equal("-qec", "stty cols 1000 rows 50 2>/dev/null; claude setup-token", "/dev/null");
        var direct = ProcessAgentLoginProxy.BuildCommand("claude setup-token", pseudoTty: false);
        direct.FileName.Should().Be("claude");
        direct.Arguments.Should().Equal("setup-token");
    }

    [Fact]
    public void What_a_TUI_draws_reads_as_words()
    {
        AgentLoginTextParser.StripControl($"{Esc}7{Esc}8Welcome{Esc}[1Cto{Esc}[1CClaude{Esc}[2CCode")
            .Should().Be("Welcome to Claude  Code");
        AgentLoginTextParser.StripControl($"{Esc}[>0q").Should().BeEmpty();
        AgentLoginTextParser.StripControl($"{Esc}(B****{Esc}(B").Should().Be("****");
    }

    [Fact]
    public void A_spinner_frame_is_not_a_line_worth_showing()
    {
        foreach (var glyph in new[] { (char)0x2722, (char)0x2736, (char)0x273B, (char)0x273D, (char)0x00B7, '*' })
        {
            AgentLoginTextParser.IsSpinnerFrame(glyph.ToString()).Should().BeTrue();
        }

        AgentLoginTextParser.IsSpinnerFrame("Paste code here if prompted >").Should().BeFalse();
        AgentLoginTextParser.IsSpinnerFrame("****************************************").Should().BeFalse("a masked code is not a spinner");
    }
}
