using FluentAssertions;
using GwsBusinessSuite.Application.DevTools;

namespace GwsBusinessSuite.Tests;

public sealed class DevToolsEncodingTests
{
    [Fact]
    public void Base64_ShouldRoundTrip()
    {
        var encoded = DevToolsEncoding.Base64Encode("Sentinel rocks");
        encoded.Success.Should().BeTrue();
        DevToolsEncoding.Base64Decode(encoded.Output).Output.Should().Be("Sentinel rocks");
    }

    [Fact]
    public void Base64Decode_ShouldFailCleanlyOnInvalidInput()
    {
        var result = DevToolsEncoding.Base64Decode("not base64!!!");
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void UrlEncoding_ShouldRoundTrip()
    {
        const string original = "hello world & friends?";
        var encoded = DevToolsEncoding.UrlEncode(original);
        DevToolsEncoding.UrlDecode(encoded.Output).Output.Should().Be(original);
    }

    [Fact]
    public void HtmlEncoding_ShouldRoundTrip()
    {
        const string original = "<script>alert('x')</script>";
        var encoded = DevToolsEncoding.HtmlEncode(original);
        encoded.Output.Should().NotContain("<script>");
        DevToolsEncoding.HtmlDecode(encoded.Output).Output.Should().Be(original);
    }

    [Fact]
    public void GZip_ShouldRoundTrip()
    {
        const string original = "The quick brown fox jumps over the lazy dog, repeated many times.";
        var compressed = DevToolsEncoding.GZipCompress(original);
        compressed.Success.Should().BeTrue();
        DevToolsEncoding.GZipDecompress(compressed.Output).Output.Should().Be(original);
    }

    [Fact]
    public void GZipDecompress_ShouldFailCleanlyOnNonGZipData()
    {
        var result = DevToolsEncoding.GZipDecompress(Convert.ToBase64String("not gzip"u8.ToArray()));
        result.Success.Should().BeFalse();
    }

    [Fact]
    public void JwtDecode_ShouldDecodeHeaderAndPayloadWithoutVerifyingSignature()
    {
        // { "alg": "HS256", "typ": "JWT" } . { "sub": "1234567890", "name": "Grant" } . (fake signature)
        const string token = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9." +
                              "eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkdyYW50In0." +
                              "not-a-real-signature";

        var result = DevToolsEncoding.JwtDecode(token);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("HS256").And.Contain("Grant").And.Contain("Signature not verified");
    }

    [Fact]
    public void JwtDecode_ShouldFailCleanlyWithFewerThanTwoSegments()
    {
        DevToolsEncoding.JwtDecode("onlyonesegment").Success.Should().BeFalse();
    }

    [Fact]
    public void GenerateQrCodePng_ShouldProduceNonEmptyPngBytes()
    {
        var result = DevToolsEncoding.GenerateQrCodePng("https://example.com");

        result.Success.Should().BeTrue();
        result.Bytes.Should().NotBeNull();
        result.Bytes!.Length.Should().BeGreaterThan(0);
        // PNG magic bytes
        result.Bytes[..8].Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
    }

    [Fact]
    public void GenerateQrCodePng_ShouldRejectEmptyInput()
    {
        DevToolsEncoding.GenerateQrCodePng("   ").Success.Should().BeFalse();
    }
}
