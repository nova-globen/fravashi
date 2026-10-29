using AwesomeAssertions;
using Fravashi.Domain.AgentSessions;
using Xunit;

namespace Fravashi.Domain.Tests;

/// <summary>Backlog 26: what a pasted image has to be before it is sent to the agent or kept in a transcript.</summary>
public class AgentImageAttachmentTests
{
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    public static TheoryData<byte[], string> Signatures => new()
    {
        { [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0], "image/png" },
        { [0xFF, 0xD8, 0xFF, 0xE0, 0], "image/jpeg" },
        { [.. "GIF89a"u8, 0], "image/gif" },
        { [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8], "image/webp" },
    };

    [Theory]
    [MemberData(nameof(Signatures))]
    public void The_media_type_comes_from_the_bytes(byte[] bytes, string expected)
    {
        AgentImageAttachment.TryCreate(bytes, out var error)!.MediaType.Should().Be(expected);
        error.Should().BeNull();
    }

    [Fact]
    public void Bytes_that_are_not_a_supported_image_are_refused_in_words()
    {
        AgentImageAttachment.TryCreate("<svg/>"u8, out var error).Should().BeNull();
        error.Should().Contain("PNG, JPEG, GIF and WebP");

        AgentImageAttachment.TryCreate([], out error).Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void An_image_over_the_limit_is_refused_and_one_at_the_limit_is_not()
    {
        var atLimit = new byte[AgentImageAttachment.MaxBytes];
        PngHeader.CopyTo(atLimit, 0);
        AgentImageAttachment.TryCreate(atLimit, out _).Should().NotBeNull();

        var over = new byte[AgentImageAttachment.MaxBytes + 1];
        PngHeader.CopyTo(over, 0);
        AgentImageAttachment.TryCreate(over, out var error).Should().BeNull();
        error.Should().Contain("limit is 3.5 MB");
    }

    [Fact]
    public void The_byte_count_and_data_uri_describe_the_image()
    {
        var image = AgentImageAttachment.TryCreate(PngHeader, out _)!;

        image.ByteCount.Should().Be(PngHeader.Length);
        image.DataUri.Should().Be("data:image/png;base64," + Convert.ToBase64String(PngHeader));
    }

    [Fact]
    public void A_stored_row_is_trusted_only_when_its_bytes_are_what_it_claims()
    {
        var data = Convert.ToBase64String(PngHeader);

        AgentImageAttachment.FromStored("image/png", data).Should().NotBeNull();
        AgentImageAttachment.FromStored("image/jpeg", data).Should().BeNull("the bytes are a PNG");
        AgentImageAttachment.FromStored("image/png", "not base64!").Should().BeNull();
        AgentImageAttachment.FromStored(null, data).Should().BeNull();
    }
}
