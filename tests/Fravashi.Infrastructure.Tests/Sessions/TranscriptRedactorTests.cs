using AwesomeAssertions;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Backlog 102, rounds two, three and final: the redactor's policy after the adversarial reviews
/// that broke rounds one and two. Provider-prefix patterns (sk-ant-, ghp_ family, xox*/xapp-,
/// glpat-, AKIA) are PRIMARY: they match anywhere in text, are never guarded, and tolerate terminal
/// wrapping as JOINED RUNS (round three): charset runs joined by single line breaks, where a joiner
/// is a raw break (\r\n, \n, a lone \r, and since backlog 119 U+000B, U+000C, U+0085, U+2028 and
/// U+2029) OR its escaped two-character text form (a literal backslash-n, backslash-r,
/// backslash-r-backslash-n), because a key inside double-encoded JSON carries its break as text and
/// round three leaked its tail, optionally followed by indentation (backlog 119). The final round's
/// six-join cap became a material ceiling in backlog 119. The env-name rule is secondary and
/// decorated (export/declare -x/diff/quote prefixes, quoted values, trailing comments, JSON strings
/// whose values consume their escapes) but guarded against placeholder, passthrough and path
/// values, because a NAME saying TOKEN is only a hint. Every case here that reproduces a review
/// finding names it; the leaks and the false positives both came out of the compiled harnesses, not
/// out of imagination.
/// </summary>
public class TranscriptRedactorTests
{
    // Not a real key: the shape is real (sk-ant- then its charset), the material is the word "Fake".
    private const string FakeMaterial = "Fake0123456789Fake0123456789Fake0123456789FakeAA";
    private const string FakeKey = "sk-ant-api03-" + FakeMaterial;

    private static string Wrap(string value, int width)
    {
        var lines = new List<string>();
        for (var i = 0; i < value.Length; i += width)
        {
            lines.Add(value.Substring(i, Math.Min(width, value.Length - i)));
        }

        return string.Join('\n', lines);
    }

    // ---------------------------------------------------------------- provider prefixes, primary

    [Fact]
    public void An_env_dump_line_keeps_the_name_and_replaces_the_value()
    {
        // The key sits on the LAST line here on purpose: a key line in the middle of a dump is the
        // documented joined-run acceptance below, and this test pins the clean name-kept shape.
        var dump = "PATH=/usr/local/bin:/usr/bin\nHOME=/root\nANTHROPIC_API_KEY=" + FakeKey;

        var redacted = TranscriptRedactor.Redact(dump);

        redacted.Should().Contain("ANTHROPIC_API_KEY=[redacted:ANTHROPIC_API_KEY]");
        redacted.Should().NotContain(FakeMaterial);
        redacted.Should().Contain("PATH=/usr/local/bin:/usr/bin");
        redacted.Should().Contain("HOME=/root");
    }

    [Fact]
    public void A_bare_key_outside_any_env_line_is_replaced()
    {
        var text = "the process was started with " + FakeKey + " in its arguments";

        var redacted = TranscriptRedactor.Redact(text);

        redacted.Should().NotContain(FakeMaterial);
        redacted.Should().Contain("[redacted:sk-ant-api-key]");
    }

    [Theory]
    [InlineData("ghp_SecretMaterialFake12345")]
    [InlineData("gho_SecretMaterialFake12345")]
    [InlineData("ghu_SecretMaterialFake12345")]
    [InlineData("ghs_SecretMaterialFake12345")]
    [InlineData("github_" + "pat_SecretMaterialFake12345_MoreFakeMaterial")]
    public void A_github_token_is_redacted_anywhere_in_text(string token)
    {
        var text = "the request used " + token + " as its bearer";

        var redacted = TranscriptRedactor.Redact(text);

        redacted.Should().NotContain("SecretMaterialFake12345");
        redacted.Should().Contain("[redacted:github-token]");
    }

    [Theory]
    [InlineData("xo" + "xb-1234567890-FakeSecretMaterial")]
    [InlineData("xo" + "xp-1234567890-FakeSecretMaterial")]
    [InlineData("xo" + "xa-1234567890-FakeSecretMaterial")]
    [InlineData("xapp-1234567890-FakeSecretMaterial")]
    public void A_slack_token_is_redacted_anywhere_in_text(string token)
    {
        var text = "posting with " + token + " to the channel";

        var redacted = TranscriptRedactor.Redact(text);

        redacted.Should().NotContain("FakeSecretMaterial");
        redacted.Should().Contain("[redacted:slack-token]");
    }

    [Fact]
    public void A_gitlab_pat_is_redacted_anywhere_in_text()
    {
        var text = "cloned with gl" + "pat-FakeSecretMaterial1234 over https";

        var redacted = TranscriptRedactor.Redact(text);

        redacted.Should().NotContain("gl" + "pat-FakeSecretMaterial1234");
        redacted.Should().Contain("[redacted:gitlab-pat]");
    }

    [Fact]
    public void An_aws_access_key_id_is_redacted_anywhere_in_text()
    {
        // The canonical fake id from the AWS documentation: AKIA then exactly 16 of [A-Z0-9].
        var text = "aws_access_key_id = AK" + "IAIOSFODNN7EXAMPLE";

        var redacted = TranscriptRedactor.Redact(text);

        redacted.Should().NotContain("AK" + "IAIOSFODNN7EXAMPLE");
        redacted.Should().Contain("[redacted:aws-access-key-id]");
    }

    [Theory]
    [InlineData("xghp_NotATokenJustAnIdentifier12")]
    [InlineData("my_github_pat_lookalike_variable_name_here")]
    [InlineData("prefix-xo" + "xb-12345678901234567890")]
    [InlineData("XAKIAIOSFODNN7EXAMPLE")]
    public void A_prefix_embedded_in_a_longer_identifier_is_untouched(string identifier)
    {
        // The lookbehind: a provider prefix inside a longer identifier is not a token boundary.
        var text = "var " + identifier + " = 1;";

        TranscriptRedactor.Redact(text).Should().Be(text);
    }

    [Theory]
    [InlineData("the prefix sk-ant-api03 by itself is not a key")]
    [InlineData("a short ghp_abc is not a token")]
    [InlineData("channel id xoxb-1234 alone is below the floor")]
    [InlineData("glpat-tooshort is below the gitlab floor")]
    [InlineData("AKIA1234 is not an access key id")]
    public void A_provider_lookalike_below_its_length_floor_is_untouched(string text)
    {
        TranscriptRedactor.Redact(text).Should().Be(text);
    }

    // ------------------------- wrap tolerance as joined runs (round two leaks 40, 41; round three A/B)

    [Fact]
    public void A_key_wrapped_right_after_its_prefix_is_redacted()
    {
        // Round-two harness "sk-ant wrapped right after prefix": terminal capture broke the line
        // between the prefix and the material, and the whole key leaked.
        var text = "key is sk-ant-\napi03-" + FakeMaterial;

        var redacted = TranscriptRedactor.Redact(text);

        redacted.Should().NotContain(FakeMaterial);
        redacted.Should().Contain("[redacted:sk-ant-api-key]");
    }

    [Fact]
    public void A_key_wrapped_inside_its_material_is_redacted()
    {
        // Round-two harness "sk-ant wrapped after 10 chars": the break sat inside the material and
        // everything after it leaked.
        var text = "key is sk-ant-api03-Fake\n" + FakeMaterial;

        var redacted = TranscriptRedactor.Redact(text);

        redacted.Should().NotContain(FakeMaterial[4..]);
        redacted.Should().Contain("[redacted:sk-ant-api-key]");
    }

    [Fact]
    public void A_key_wrapped_after_sixteen_or_more_material_chars_is_redacted_whole()
    {
        // Round-three harness A1: an 80-column terminal breaks the value after 16+ material chars.
        // Round two's wrapped alternative allowed the break only in the first 15 chars, so the
        // contiguous alternative matched line one and the tail on line two leaked.
        var text = "ANTHROPIC_API_KEY=sk-" + "ant-api03-Fake0123456789\nFake0123456789Fake0123456789FakeAA";

        var redacted = TranscriptRedactor.Redact(text);

        redacted.Should().NotContain("Fake0123456789Fake0123456789FakeAA");
        redacted.Should().Be("ANTHROPIC_API_KEY=[redacted:ANTHROPIC_API_KEY]");
    }

    [Fact]
    public void A_github_token_wrapped_after_a_long_first_segment_is_redacted_whole()
    {
        // Round-three harness A2.
        var redacted = TranscriptRedactor.Redact("token ghp_SecretMaterialABCDEF\nGHIJKLMNOPqrstuvwx1234 more");

        redacted.Should().NotContain("GHIJKLMNOPqrstuvwx1234");
        redacted.Should().Be("token [redacted:github-token] more");
    }

    [Fact]
    public void A_key_wrapped_across_two_line_breaks_is_redacted_whole()
    {
        // Round-three harness A3: a narrow pane wraps twice; round two tolerated exactly one break,
        // so everything after the second one leaked. Joined runs allow up to three joins.
        var redacted = TranscriptRedactor.Redact("key sk-ant-\napi03Fake\n0123456789Fake0123456789FakeAA");

        redacted.Should().NotContain("0123456789Fake0123456789FakeAA");
        redacted.Should().Be("key [redacted:sk-ant-api-key]");
    }

    [Fact]
    public void A_gitlab_pat_wrapped_across_two_line_breaks_is_redacted_whole()
    {
        // Round-three harness A4.
        var redacted = TranscriptRedactor.Redact("glpat-\nAbCd\nEfGhIjKlMnOpQrStUvWx1234");

        redacted.Should().NotContain("EfGhIjKlMnOpQrStUvWx1234");
        redacted.Should().Be("[redacted:gitlab-pat]");
    }

    [Fact]
    public void A_key_wrapped_on_a_lone_carriage_return_is_redacted()
    {
        // Round-three harness B2: the round-two joiner was \r?\n, so an old-mac lone \r capture
        // leaked the whole key. A lone \r is a joiner in its own right now.
        var redacted = TranscriptRedactor.Redact("key sk-ant-Fake\r" + FakeMaterial);

        redacted.Should().NotContain(FakeMaterial);
        redacted.Should().Be("key [redacted:sk-ant-api-key]");
    }

    [Fact]
    public void A_key_wrapped_on_a_unicode_line_separator_is_redacted()
    {
        // Round-three harness B3: U+2028 is a line break to anything that renders the transcript,
        // so it is a joiner too.
        var redacted = TranscriptRedactor.Redact("key sk-ant-Fake\u2028" + FakeMaterial);

        redacted.Should().NotContain(FakeMaterial);
        redacted.Should().Be("key [redacted:sk-ant-api-key]");
    }

    [Fact]
    public void A_full_length_key_wrapped_at_eighty_columns_in_an_env_dump_is_redacted_whole()
    {
        // Round-three harness J1: the realistic incident shape, a 93-char value wrapped by an
        // 80-column terminal, 31 chars of key material on the continuation line.
        var value = "sk-ant-api03-" + new string('X', 80);
        var text = "ANTHROPIC_API_KEY=" + value[..62] + "\n" + value[62..];

        var redacted = TranscriptRedactor.Redact(text);

        redacted.Should().NotContain("XXXXX");
        redacted.Should().Be("ANTHROPIC_API_KEY=[redacted:ANTHROPIC_API_KEY]");
    }

    [Fact]
    public void A_wrapped_lookalike_below_the_length_floor_is_untouched()
    {
        // Wrap tolerance must not glue prose across a newline into a phantom key.
        var text = "voice sk-ant-agent\nTOKENS are documented";

        TranscriptRedactor.Redact(text).Should().Be(text);
    }

    [Fact]
    public void A_key_wrapped_by_an_escaped_line_break_inside_nested_json_is_redacted_whole()
    {
        // Round-three harness P4, the final round's FIX: a key inside double-encoded JSON carries
        // its wrap as the two-character text \n, which round three's raw-only joiner set did not
        // know, so the tail after the escape leaked. The escaped sequences are joiners now.
        var input = "{\"output\":\"key sk-" + "ant-api03-Fake0123456789\\nFake0123456789Fake0123456789FakeAA was used\"}";

        var redacted = TranscriptRedactor.Redact(input);

        redacted.Should().NotContain("Fake0123456789Fake0123456789FakeAA");
        redacted.Should().Be("{\"output\":\"key [redacted:sk-ant-api-key] was used\"}");
    }

    [Fact]
    public void A_key_whose_first_run_is_under_the_floor_still_joins_across_an_escaped_break()
    {
        // Round-three harness P5: the escaped break sits four chars into the material, so neither
        // side alone reaches the floor and round three leaked the WHOLE key, not just a tail.
        var input = "{\"output\":\"key sk-ant-Fake\\n" + FakeMaterial + " was used\"}";

        var redacted = TranscriptRedactor.Redact(input);

        redacted.Should().NotContain(FakeMaterial);
        redacted.Should().Be("{\"output\":\"key [redacted:sk-ant-api-key] was used\"}");
    }

    [Fact]
    public void A_key_wrapped_by_an_escaped_crlf_is_redacted_whole_as_one_join()
    {
        // Round-three harness P7: the four-character \r\n text form is ONE joiner, not two, so it
        // costs one join against the cap exactly like the raw CRLF it encodes.
        var input = "{\"log\":\"sk-" + "ant-api03-Fake0123456789\\r\\nFake0123456789Fake0123456789FakeAA\"}";

        var redacted = TranscriptRedactor.Redact(input);

        redacted.Should().NotContain("Fake0123456789Fake0123456789FakeAA");
        redacted.Should().Be("{\"log\":\"[redacted:sk-ant-api-key]\"}");
    }

    // ------------------------------------------------- backlog 119: the shapes the final round missed

    [Theory]
    [InlineData('\f')]
    [InlineData('\u0085')]
    [InlineData('\u2029')]
    [InlineData('\v')]
    public void A_key_wrapped_on_another_line_break_after_a_long_head_is_redacted_whole(char wrap)
    {
        // Backlog 119, shape one, measured through the diagnostic bundle while closing item 74: the
        // raw joiners were \r\n, \n, a lone \r and U+2028, so a wrap on U+000C, U+0085 or U+2029 (and
        // the vertical tab, the same family) was no joiner at all. This head carries 20 material chars,
        // over the floor on its own, so it was replaced and the tail after the wrap rode on in the clear.
        const string head = "sk-" + "ant-api03-Fake0123456789";
        const string tail = "Fake0123456789Fake0123456789FakeAA";

        var redacted = TranscriptRedactor.Redact("key " + head + wrap + tail + " end");

        redacted.Should().NotContain(tail);
        redacted.Should().Be("key [redacted:sk-ant-api-key] end");
    }

    [Theory]
    [InlineData('\f')]
    [InlineData('\u0085')]
    [InlineData('\u2029')]
    public void A_key_wrapped_on_another_line_break_after_a_short_head_is_redacted_whole(char wrap)
    {
        // The same shape with a head under the floor: before 119 the whole key survived a single
        // redaction pass, and only the bundle writer's normalized second pass happened to catch it.
        var redacted = TranscriptRedactor.Redact("key sk-ant-Fake" + wrap + FakeMaterial + " end");

        redacted.Should().NotContain(FakeMaterial);
        redacted.Should().Be("key [redacted:sk-ant-api-key] end");
    }

    [Theory]
    [InlineData("    ")]
    [InlineData("\t")]
    [InlineData(" \t ")]
    public void A_continuation_line_that_starts_with_whitespace_after_a_long_head_is_redacted_whole(string indent)
    {
        // Backlog 119, shape two: a pane or a logger that indents its continuation lines. The joiner
        // required charset material straight after the break, so the head (over the floor) was
        // replaced and the indented tail survived.
        const string tail = "Fake0123456789Fake0123456789FakeAA";

        var redacted = TranscriptRedactor.Redact("key sk-" + "ant-api03-Fake0123456789\n" + indent + tail + " end");

        redacted.Should().NotContain(tail);
        redacted.Should().Be("key [redacted:sk-ant-api-key] end");
    }

    [Fact]
    public void A_continuation_line_that_starts_with_whitespace_after_a_short_head_is_redacted_whole()
    {
        // The short-head facet of shape two: neither side reached a floor on its own and the WHOLE
        // key survived.
        var redacted = TranscriptRedactor.Redact("key sk-ant-Fake\r\n  " + FakeMaterial);

        redacted.Should().NotContain(FakeMaterial);
        redacted.Should().Be("key [redacted:sk-ant-api-key]");
    }

    [Fact]
    public void An_indented_continuation_inside_escaped_json_is_redacted_whole()
    {
        // Shape two carried as text: an escaped break followed by spaces or an escaped tab.
        var input = "{\"log\":\"tok ghp_SecretMaterialABCDEF\\n  GHIJKLMNOP1234\\n\\tQRSTUV5678 done\"}";

        var redacted = TranscriptRedactor.Redact(input);

        redacted.Should().NotContain("GHIJKLMNOP1234");
        redacted.Should().NotContain("QRSTUV5678");
        redacted.Should().Be("{\"log\":\"tok [redacted:github-token] done\"}");
    }

    [Theory]
    [InlineData(10)]
    [InlineData(3)]
    [InlineData(1)]
    public void A_key_wrapped_past_the_old_join_cap_is_redacted_whole(int width)
    {
        // Backlog 119, shape three: a wrap past the old cap of six joins kept its tail. A 106-char key
        // at 10 columns needs ten joins, at 1 column it needs 105. The cap is now the material ceiling
        // (see the class doc), so any wrap of a key that fits the ceiling redacts whole. The prefix is
        // kept on one line: a prefix split by a wrap was never a match and is not claimed here.
        var key = "sk-ant-api03-" + string.Concat(Enumerable.Repeat("Fake567890", 9)) + "ABC";

        var once = TranscriptRedactor.Redact("key sk-ant-" + Wrap(key[7..], width) + "\n# next line");

        once.Should().Be("key [redacted:sk-ant-api-key]\n# next line");
        TranscriptRedactor.Redact(once).Should().Be(once);
    }

    [Theory]
    [InlineData("key sk-" + "ant-api03-Fake0123456789\n\nFake0123456789Fake0123456789FakeAA", "Fake0123456789Fake0123456789FakeAA")]
    [InlineData("key sk-" + "ant-api03-Fake0123456789 \nFake0123456789Fake0123456789FakeAA", "Fake0123456789Fake0123456789FakeAA")]
    [InlineData("key sk-a\nnt-api03-Fake0123456789Fake0123456789FakeAA", "api03-Fake0123456789Fake0123456789FakeAA")]
    public void What_the_redactor_still_does_not_join_after_backlog_119_is_pinned(string input, string survivor)
    {
        // KNOWN, stated in the bundle writer's doc and the Settings copy, asserted as it MEASURES: a
        // blank line between two runs (two breaks), trailing whitespace BEFORE the break, and a
        // provider prefix that is itself split by the wrap are not joins. Each would widen the swallow
        // into ordinary paragraphs and prose, which is the trade 119 did not take.
        TranscriptRedactor.Redact(input).Should().Contain(survivor);
    }

    [Fact]
    public void An_aws_key_id_wrapped_one_char_per_line_is_redacted_whole()
    {
        var redacted = TranscriptRedactor.Redact("id AKIA" + Wrap("IOSFODNN7EXAMPLE", 1) + " set");

        redacted.Should().Be("id [redacted:aws-access-key-id] set");
    }

    [Fact]
    public void A_key_wrapped_at_ten_columns_needing_six_joins_is_redacted_whole()
    {
        // Round-three harness K1: a 61-char key wrapped at 10 columns is seven lines, six joins,
        // exactly at the raised cap (final round; the cap of three left four lines of material raw).
        var text = "key " + Wrap(FakeKey, 10) + " end";

        var redacted = TranscriptRedactor.Redact(text);

        redacted.Should().NotContain("FakeAA");
        redacted.Should().Be("key [redacted:sk-ant-api-key] end");
    }

    [Fact]
    public void A_106_char_key_wrapped_at_twenty_five_columns_is_redacted_whole()
    {
        // Round-three harness K3: five lines, four joins, inside the raised cap (it leaked at three).
        var key = "sk-ant-api03-" + string.Concat(Enumerable.Repeat("Fake567890", 9)) + "ABC";

        var redacted = TranscriptRedactor.Redact(Wrap(key, 25));

        redacted.Should().Be("[redacted:sk-ant-api-key]");
    }

    [Fact]
    public void A_ten_column_wrap_of_a_106_char_key_is_redacted_whole()
    {
        // Round-three harness K2. Until backlog 119 this was the ACCEPTED bound: the join cap was six
        // and this eleven-line wrap kept four lines of key material in the clear. The cap is now the
        // material ceiling, so the same input redacts whole, and the output is still idempotent.
        var key = "sk-ant-api03-" + string.Concat(Enumerable.Repeat("Fake567890", 9)) + "ABC";
        var wrapped = Wrap(key, 10);

        var once = TranscriptRedactor.Redact(wrapped);
        var twice = TranscriptRedactor.Redact(once);

        once.Should().Be("[redacted:sk-ant-api-key]");
        twice.Should().Be(once);
    }

    [Fact]
    public void A_key_ending_exactly_at_a_line_end_may_swallow_the_next_lines_first_word()
    {
        // ACCEPTED by the round-three policy (same trade as the fenced-docs case below): joined-run
        // wrap tolerance cannot tell a wrapped key from a key that happens to end at a line end, so
        // when the next line begins with charset text the join swallows that line's first word up to
        // its first non-charset character. Security-primary patterns take the over-redaction; the
        // damage is bounded by the per-run length cap and (since backlog 119) the material ceiling,
        // and the rest of the line survives.
        var redacted = TranscriptRedactor.Redact("ANTHROPIC_API_KEY=" + FakeKey + "\nHOME=/root");

        redacted.Should().NotContain(FakeMaterial);
        redacted.Should().Be("ANTHROPIC_API_KEY=[redacted:sk-ant-api-key]=/root");
    }

    [Fact]
    public void A_crlf_key_line_is_redacted_with_the_same_join_acceptance()
    {
        // CRLF is one joiner, not two; the swallow of the next line's first word is the documented
        // acceptance above.
        var redacted = TranscriptRedactor.Redact("GITHUB_TOKEN=ghp_SecretMaterial12345\r\nHOME=/root");

        redacted.Should().NotContain("ghp_SecretMaterial12345");
        redacted.Should().Be("GITHUB_TOKEN=[redacted:github-token]=/root");
    }

    [Fact]
    public void Eight_joined_lines_under_the_material_ceiling_redact_whole_and_a_second_pass_cannot_cross_the_marker()
    {
        // Idempotence re-proof (final round): the marker's own text is ":" and "]" around charset
        // words, and neither is a joiner (raw or escaped) or charset, so the joined-runs shape can
        // never continue from inside a marker into following text (the sk-ant marker's own "api-key"
        // tail is 7 chars, under every floor). Before backlog 119 the eighth line here sat past the
        // six-join cap and stayed raw; 144 material chars are under the 256 ceiling, so it joins now.
        var text = "key sk-ant-Aaaa0123456789aaaa\nBbbb0123456789bbbb\nCccc0123456789cccc" +
                   "\nDddd0123456789dddd\nEeee0123456789eeee\nFfff0123456789ffff" +
                   "\nGggg0123456789gggg\nHhhh0123456789hhhh\n# after";

        var once = TranscriptRedactor.Redact(text);
        var twice = TranscriptRedactor.Redact(once);

        once.Should().Be("key [redacted:sk-ant-api-key]\n# after");
        twice.Should().Be(once);
    }

    [Fact]
    public void The_material_ceiling_ends_the_marker_at_the_run_that_reaches_it_and_hands_back_the_rest()
    {
        // Backlog 119's replacement for the join cap, and its measured over-redaction bound: twenty
        // charset-only lines of 20 chars after the prefix. The ceiling (256 for sk-ant-) is reached
        // inside the thirteenth line, so the marker covers thirteen lines and the other seven come back
        // exactly as written. Nothing a real key could be is declined: the ceiling IS the longest
        // material the pattern accepts on one line.
        var lines = Enumerable.Range(0, 20).Select(i => "Line" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + "abcdefghijklmn").ToList();
        var text = "key sk-ant-" + string.Join('\n', lines) + "\n# after";

        var once = TranscriptRedactor.Redact(text);

        once.Should().Be("key [redacted:sk-ant-api-key]\n" + string.Join('\n', lines.Skip(13)) + "\n# after");
        TranscriptRedactor.Redact(once).Should().Be(once);
    }

    [Fact]
    public void An_aws_key_id_hands_back_a_following_uppercase_line_past_its_ceiling()
    {
        // AKIA's ceiling is 16, its exact length, so the uppercase word a join picks up after a
        // complete key id is handed back rather than swallowed (it was swallowed before backlog 119).
        var redacted = TranscriptRedactor.Redact("AK" + "IAIOSFODNN7EXAMPLE\nREGION us-east-1");

        redacted.Should().Be("[redacted:aws-access-key-id]\nREGION us-east-1");
    }

    [Fact]
    public void A_key_ending_at_a_line_end_swallows_the_first_word_of_an_indented_next_line()
    {
        // ACCEPTED, the new cost of backlog 119's indentation tolerance, measured: a key at the end of
        // a YAML-style line joins into an indented next line's first charset word, which stops at the
        // first non-charset character (the colon here) so the rest of that line survives.
        var redacted = TranscriptRedactor.Redact("api_key: " + FakeKey + "\n  model: haiku");

        redacted.Should().NotContain(FakeMaterial);
        redacted.Should().Be("api_key: [redacted:sk-ant-api-key]: haiku");
    }

    [Fact]
    public void A_key_followed_by_a_space_and_a_word_on_the_same_line_does_not_swallow_the_word()
    {
        // Indentation joins only AFTER a break: whitespace on its own still ends a match.
        var redacted = TranscriptRedactor.Redact("key " + FakeKey + " was rotated");

        redacted.Should().Be("key [redacted:sk-ant-api-key] was rotated");
    }

    [Fact]
    public void Adversarial_joiner_heavy_input_redacts_in_bounded_time()
    {
        // The joins are no longer capped at six, so the nested quantifiers are re-proven linear on the
        // shapes that would hurt a backtracking engine: thousands of prefixes each followed by long
        // chains of one-char runs and indentation, and AKIA chains whose trailing lookahead fails.
        var chain = "sk-ant-" + string.Concat(Enumerable.Repeat("a\n \t", 400)) + "!";
        var aws = "AKIA" + string.Concat(Enumerable.Repeat("A\n", 20)) + "Aa ";
        var text = string.Concat(Enumerable.Repeat(chain + aws, 300));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var redacted = TranscriptRedactor.Redact(text);
        watch.Stop();

        redacted.Should().Contain("[redacted:sk-ant-api-key]");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void A_marker_followed_by_an_escaped_line_break_and_charset_text_is_not_rematched()
    {
        // Idempotence re-proof for the NEW joiners: an escaped \n directly after a marker must not
        // let the marker's "api-key" tail join into following charset text, because ']' sits between
        // them and ']' is neither charset nor joiner.
        var text = "[redacted:sk-ant-api-key]\\nFake0123456789Fake0123456789";

        TranscriptRedactor.Redact(text).Should().Be(text);
    }

    // -------------------------------------------- decorated env lines (review leaks 30 to 38, 42)

    [Theory]
    [InlineData("GITHUB_TOKEN=gh" + "p_SecretMaterial12345")]
    [InlineData("GITHUB_TOKEN=gh" + "p_SecretMaterial12345 ")]
    [InlineData("GITHUB_TOKEN=ghp_SecretMaterial12345\t")]
    [InlineData("export GITHUB_TOKEN=gh" + "p_SecretMaterial12345")]
    [InlineData("declare -x GITHUB_TOKEN=\"ghp_SecretMaterial12345\"")]
    [InlineData("+GITHUB_TOKEN=gh" + "p_SecretMaterial12345")]
    [InlineData("{\"Env\":[\"GITHUB_TOKEN=ghp_SecretMaterial12345\"]}")]
    [InlineData("{\"cmd\":\"GITHUB_TOKEN=gh" + "p_SecretMaterial12345 npm publish\"}")]
    public void A_decorated_env_line_no_longer_leaks(string input)
    {
        // Review harness: trailing whitespace, export, declare -x, diff and JSON decorations all
        // slipped past the bare line anchor in round one.
        var redacted = TranscriptRedactor.Redact(input);

        redacted.Should().NotContain("ghp_SecretMaterial12345");
        redacted.Should().Contain("GITHUB_TOKEN=");
    }

    [Fact]
    public void A_quoted_value_containing_a_space_is_redacted_inside_its_quotes()
    {
        // Review harness "quoted value containing a space": the space ended the old value match.
        var redacted = TranscriptRedactor.Redact("SLACK_TOKEN=\"xoxb-1234 secretpart-abcdef\"");

        redacted.Should().NotContain("secretpart-abcdef");
        redacted.Should().Be("SLACK_TOKEN=\"[redacted:SLACK_TOKEN]\"");
    }

    [Fact]
    public void A_value_followed_by_an_inline_comment_is_redacted()
    {
        // Review harness "value then trailing comment": the comment kept the value off end-of-line.
        var redacted = TranscriptRedactor.Redact("AWS_SECRET_ACCESS_KEY=" + "wJalrXUtnFEMIK7MDENG # prod");

        redacted.Should().NotContain("wJalrXUtnFEMIK7MDENG");
        redacted.Should().Be("AWS_SECRET_ACCESS_KEY=[redacted:AWS_SECRET_ACCESS_KEY] # prod");
    }

    [Theory]
    [InlineData("{\"env\":\"API_KEY=abcdef\\\"ghijkl12345\"}", "ghijkl12345")]
    [InlineData("{\"Env\":[\"SECRET_TOKEN=aaaaaaaa\\\"bbbbbbbb\"]}", "bbbbbbbb")]
    public void A_json_env_value_with_an_escaped_quote_is_redacted_whole(string input, string tail)
    {
        // Round-three harness D1/D2: the JSON value pattern used to STOP at the backslash, so the
        // head redacted (or nothing did) and the tail after the escaped quote leaked. The value now
        // consumes escape pairs, so the whole thing is the value and the whole thing redacts.
        var redacted = TranscriptRedactor.Redact(input);

        redacted.Should().NotContain(tail);
        redacted.Should().Contain("[redacted:");
    }

    [Fact]
    public void A_json_env_value_with_an_escaped_quote_mid_secret_is_redacted_whole()
    {
        // Round-three harness J2, the confirmed repro: an AWS secret in a docker-inspect style Env
        // array with an embedded escaped quote; round two redacted the head and leaked the tail.
        var input = "{\"Env\":[\"AWS_SECRET_ACCESS_KEY=longenoughhead\\\"tail-abcdef123456\"]}";

        var redacted = TranscriptRedactor.Redact(input);

        redacted.Should().NotContain("longenoughhead");
        redacted.Should().NotContain("tail-abcdef123456");
        redacted.Should().Be("{\"Env\":[\"AWS_SECRET_ACCESS_KEY=[redacted:AWS_SECRET_ACCESS_KEY]\"]}");
    }

    [Fact]
    public void A_json_env_value_with_an_escaped_backslash_is_redacted_whole()
    {
        // The other escape shape: a doubled backslash inside the value is consumed as one escape
        // pair, not treated as the end of the value.
        var input = "{\"Env\":[\"MY_SECRET=head1234\\\\tail5678\"]}";

        var redacted = TranscriptRedactor.Redact(input);

        redacted.Should().NotContain("head1234");
        redacted.Should().NotContain("tail5678");
        redacted.Should().Be("{\"Env\":[\"MY_SECRET=[redacted:MY_SECRET]\"]}");
    }

    [Fact]
    public void An_indented_env_dump_line_is_redacted()
    {
        // Round one called indentation "code, not an env dump" and the review used exactly that:
        // terminal output indents env dumps all the time, so the decorated anchor now allows leading
        // whitespace and this line redacts. The old test asserting the opposite is gone on purpose.
        var redacted = TranscriptRedactor.Redact("    GITHUB_TOKEN=abcdefghijkl");

        redacted.Should().Be("    GITHUB_TOKEN=[redacted:GITHUB_TOKEN]");
    }

    [Fact]
    public void A_token_named_env_line_is_redacted_by_its_name()
    {
        TranscriptRedactor.Redact("GITHUB_TOKEN=gh" + "p_0123456789abcdef")
            .Should().Be("GITHUB_TOKEN=[redacted:GITHUB_TOKEN]");
    }

    // --------------------------------------------- non-secret value guards (review false positives)

    [Theory]
    [InlineData("OPENAI_API_KEY=your-key-goes-here")]
    [InlineData("NPM_TOKEN=${NPM_TOKEN}")]
    [InlineData("GITHUB_TOKEN=$GITHUB_TOKEN")]
    [InlineData("AUTH_TOKEN=$(get_token)\ncurl -H \"Authorization: $AUTH_TOKEN\"")]
    [InlineData("DEPLOY_KEY=deploy/id_rsa.pub")]
    public void A_review_confirmed_false_positive_round_trips_byte_identical(string input)
    {
        // Review harness: every one of these was mangled in round one. Passthroughs ($...), the
        // your- placeholder and path values are not secrets whatever the name says.
        TranscriptRedactor.Redact(input).Should().Be(input);
    }

    [Theory]
    [InlineData("API_KEY=changeme-now")]
    [InlineData("API_KEY=see-example-value")]
    [InlineData("MY_TOKEN=placeholder-here")]
    [InlineData("MY_TOKEN=dummy-value-1")]
    [InlineData("MY_TOKEN=<your-token-here>")]
    [InlineData("GITHUB_TOKEN=GITHUB_TOKEN")]
    [InlineData("USE_TOKEN=1")]
    [InlineData("API_KEY=1234567")]
    public void A_placeholder_or_trivial_value_is_untouched(string input)
    {
        TranscriptRedactor.Redact(input).Should().Be(input);
    }

    [Fact]
    public void Ordinary_code_and_prose_mentioning_token_are_untouched()
    {
        var code =
            "var TOKEN = ReadToken();\n" +
            "// the TOKEN is read from configuration at startup\n" +
            "return TOKEN;";

        TranscriptRedactor.Redact(code).Should().Be(code);
    }

    [Fact]
    public void A_git_sha_is_untouched()
    {
        var text = "commit d788bbf3c1a94be00e2f5cf05a1c6c02e6b7a4d9 reverted the change";

        TranscriptRedactor.Redact(text).Should().Be(text);
    }

    [Fact]
    public void A_lowercase_assignment_is_untouched()
    {
        var line = "my_token=abcdefghijkl";

        TranscriptRedactor.Redact(line).Should().Be(line);
    }

    [Fact]
    public void Prose_naming_the_sk_ant_pattern_is_untouched()
    {
        // This repository's own documentation names the prefix; the bare prefix is not a key.
        var text = "the sk-ant- key shape length-bounded to its charset";

        TranscriptRedactor.Redact(text).Should().Be(text);
    }

    // ------------------------------------------------- accepted over-redactions (provider unguarded)

    [Fact]
    public void A_fenced_docs_example_of_a_full_key_shape_is_still_redacted()
    {
        // Accepted by the rebalanced policy: provider-prefix matches are NEVER guarded, so a docs
        // example long enough to be a real key redacts. The reviewer flagged it; the decision is
        // that a string indistinguishable from a real key gets no benefit of the doubt.
        var redacted = TranscriptRedactor.Redact("keys look like `sk-" + "ant-api03-xxxxxxxxxxxxxxxxxxxx`");

        redacted.Should().Be("keys look like `[redacted:sk-ant-api-key]`");
    }

    [Fact]
    public void A_base64_run_that_reads_as_a_key_is_still_redacted()
    {
        // Same acceptance: the base64 charset can manufacture a '+sk-ant-' boundary, and the run
        // after it is indistinguishable from key material, so it redacts.
        var redacted = TranscriptRedactor.Redact(
            "data:application/octet-stream;base64,QUJD+sk-" + "ant-AAAABBBBCCCCDDDDx0aGU=");

        redacted.Should().NotContain("sk-" + "ant-AAAABBBBCCCCDDDDx0aGU");
        redacted.Should().Contain("[redacted:sk-ant-api-key]");
    }

    [Fact]
    public void Charset_only_lines_after_a_literal_prefix_in_prose_are_swallowed_into_the_marker()
    {
        // ACCEPTED (round-three defect 3, final round decision): a literal sk-ant- in prose whose
        // tail reads as charset, followed by lines that are charset from their first character,
        // greedily joins them all until a non-charset character or the join cap stops it. This is
        // DATA LOSS in the transcript, not a leak, it needs prose shaped exactly like key material
        // to happen, and the primary-pattern policy pays over-redaction rather than risk declining
        // a real wrapped key. The swallow stops mid-line at the first space.
        var redacted = TranscriptRedactor.Redact(
            "keys use the sk-ant-prefix\nBase64LineOne\nBase64LineTwo\nthen normal prose");

        redacted.Should().Be("keys use the [redacted:sk-ant-api-key] normal prose");
    }

    [Fact]
    public void A_multi_line_env_value_keeps_its_tail_because_the_env_rule_is_single_line()
    {
        // ACCEPTED (round-three defect 4, final round decision): the env-name rule anchors to ONE
        // line by design, so a generic (non-provider) value wrapped mid-value redacts its head and
        // keeps its tail. A name is only a hint, and chasing continuation lines on a hint would
        // swallow arbitrary following output; a wrapped value that carries a provider prefix is
        // owned whole by the provider joiners instead (the wrap-tolerance tests above).
        var redacted = TranscriptRedactor.Redact(
            "CLIENT_SECRET=abcdefghijklmnopqr\nstuvwxyz0123456789");

        redacted.Should().Be("CLIENT_SECRET=[redacted:CLIENT_SECRET]\nstuvwxyz0123456789");

        // Same acceptance, quoted facet (harness P2): a quoted value wrapped mid-value has no
        // same-line closing quote, so the rule matches NOTHING and the whole value survives.
        var quoted = "export API_SECRET=\"abcdefghijklmnopqr\nstuvwxyz0123456789\"";
        TranscriptRedactor.Redact(quoted).Should().Be(quoted);
    }

    // ------------------------------------------------------- ordering (review item 103, storage cap)

    [Fact]
    public void A_secret_straddling_the_storage_cap_cut_is_redacted_before_the_cut()
    {
        // Review harness "storage cap cuts through the secret value": producers ran ForStorage
        // first and Serialize redacted afterwards, so a secret straddling the head cut was split
        // around the storage marker and its head survived. ForStorage now redacts first.
        var head = new string('a', 32_700);
        var raw = head + "\nGITHUB_TOKEN=ghp_SuperSecretMaterial1234567890\n" + new string('b', 40_000);

        var stored = AgentToolSubject.ForStorage(raw);

        stored.Should().NotContain("ghp_Su");
        stored.Should().NotContain("SuperSecretMaterial");
        stored.Length.Should().BeLessThanOrEqualTo(AgentToolSubject.MaxStoredLength);
    }

    // ----------------------------------------------------------------------------------- idempotence

    [Theory]
    [InlineData("ANTHROPIC_API_KEY=" + FakeKey + "\nand also " + FakeKey)]
    [InlineData("export GITHUB_TOKEN=gh" + "p_SecretMaterial12345")]
    [InlineData("declare -x GITHUB_TOKEN=\"ghp_SecretMaterial12345\"")]
    [InlineData("{\"Env\":[\"GITHUB_TOKEN=ghp_SecretMaterial12345\"]}")]
    [InlineData("{\"cmd\":\"GITHUB_TOKEN=gh" + "p_SecretMaterial12345 npm publish\"}")]
    [InlineData("SLACK_TOKEN=\"xoxb-1234 secretpart-abcdef\"")]
    [InlineData("AWS_SECRET_ACCESS_KEY=" + "wJalrXUtnFEMIK7MDENG # prod")]
    [InlineData("key is sk-ant-\napi03-" + FakeMaterial)]
    [InlineData("used gl" + "pat-FakeSecretMaterial1234 and AK" + "IAIOSFODNN7EXAMPLE together")]
    // Round three: every changed pattern again, joined-runs and consumed-escape shapes included.
    [InlineData("ANTHROPIC_API_KEY=sk-" + "ant-api03-Fake0123456789\nFake0123456789Fake0123456789FakeAA")]
    [InlineData("key sk-ant-\napi03Fake\n0123456789Fake0123456789FakeAA")]
    [InlineData("glpat-\nAbCd\nEfGhIjKlMnOpQrStUvWx1234")]
    [InlineData("key sk-ant-Fake\r" + FakeMaterial)]
    [InlineData("key sk-ant-Fake\u2028" + FakeMaterial)]
    [InlineData("ANTHROPIC_API_KEY=" + FakeKey + "\nHOME=/root")]
    [InlineData("GITHUB_TOKEN=ghp_SecretMaterial12345\r\nHOME=/root")]
    [InlineData("{\"env\":\"API_KEY=abcdef\\\"ghijkl12345\"}")]
    [InlineData("{\"Env\":[\"AWS_SECRET_ACCESS_KEY=longenoughhead\\\"tail-abcdef123456\"]}")]
    [InlineData("{\"Env\":[\"MY_SECRET=head1234\\\\tail5678\"]}")]
    // Final round: the escaped-joiner shapes and the env tail acceptance.
    [InlineData("{\"output\":\"key sk-ant-Fake\\n" + FakeMaterial + " was used\"}")]
    [InlineData("{\"log\":\"sk-" + "ant-api03-Fake0123456789\\r\\nFake0123456789Fake0123456789FakeAA\"}")]
    [InlineData("{\"note\":\"tok ghp_SecretMaterialABCDEF\\nGHIJKLMNOP1234 done\"}")]
    [InlineData("CLIENT_SECRET=abcdefghijklmnopqr\nstuvwxyz0123456789")]
    public void Redaction_is_idempotent(string input)
    {
        var once = TranscriptRedactor.Redact(input);

        TranscriptRedactor.Redact(once).Should().Be(once);
    }
}
