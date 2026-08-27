using System.IO.Compression;
using System.Net;
using System.Text;
using QRCoder;

namespace GwsBusinessSuite.Application.DevTools;

public static class DevToolsEncoding
{
    public static DevToolsResult Base64Encode(string input) =>
        DevToolsResult.Ok(Convert.ToBase64String(Encoding.UTF8.GetBytes(input)));

    public static DevToolsResult Base64Decode(string input)
    {
        try { return DevToolsResult.Ok(Encoding.UTF8.GetString(Convert.FromBase64String(input.Trim()))); }
        catch (FormatException) { return DevToolsResult.Fail("This isn't valid base64."); }
    }

    public static DevToolsResult UrlEncode(string input) => DevToolsResult.Ok(Uri.EscapeDataString(input));
    public static DevToolsResult UrlDecode(string input)
    {
        try { return DevToolsResult.Ok(Uri.UnescapeDataString(input)); }
        catch (UriFormatException) { return DevToolsResult.Fail("This isn't validly URL-encoded."); }
    }

    public static DevToolsResult HtmlEncode(string input) => DevToolsResult.Ok(WebUtility.HtmlEncode(input));
    public static DevToolsResult HtmlDecode(string input) => DevToolsResult.Ok(WebUtility.HtmlDecode(input));

    public static DevToolsResult GZipCompress(string input)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(input);
            gzip.Write(bytes, 0, bytes.Length);
        }
        return DevToolsResult.Ok(Convert.ToBase64String(output.ToArray()));
    }

    public static DevToolsResult GZipDecompress(string base64Input)
    {
        try
        {
            var compressed = Convert.FromBase64String(base64Input.Trim());
            using var input = new MemoryStream(compressed);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            return DevToolsResult.Ok(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException)
        {
            return DevToolsResult.Fail("This isn't valid base64-encoded GZip data.");
        }
    }

    // Decodes the header and payload segments only - the signature is never checked, since this
    // is a read-only inspection tool with no access to (and no business holding) the signing key.
    public static DevToolsResult JwtDecode(string token)
    {
        var parts = token.Trim().Split('.');
        if (parts.Length < 2)
        {
            return DevToolsResult.Fail("A JWT has at least a header and payload segment separated by '.'.");
        }

        string header, payload;
        try
        {
            header = DecodeBase64UrlSegment(parts[0]);
            payload = DecodeBase64UrlSegment(parts[1]);
        }
        catch (FormatException)
        {
            return DevToolsResult.Fail("The header or payload segment isn't valid base64url.");
        }

        var headerJson = DevToolsFormatters.FormatJson(header);
        var payloadJson = DevToolsFormatters.FormatJson(payload);
        if (!headerJson.Success || !payloadJson.Success)
        {
            return DevToolsResult.Fail("The header or payload segment isn't valid JSON once decoded.");
        }

        return DevToolsResult.Ok($"Header:\n{headerJson.Output}\n\nPayload:\n{payloadJson.Output}\n\n(Signature not verified.)");
    }

    private static string DecodeBase64UrlSegment(string segment)
    {
        var padded = segment.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    public static DevToolsImageResult GenerateQrCodePng(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return DevToolsImageResult.Fail("Enter some text or a URL to encode.");
        }
        if (text.Length > 2000)
        {
            return DevToolsImageResult.Fail("QR codes can't reliably encode more than about 2000 characters.");
        }

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data);
        return DevToolsImageResult.Ok(png.GetGraphic(10));
    }
}
