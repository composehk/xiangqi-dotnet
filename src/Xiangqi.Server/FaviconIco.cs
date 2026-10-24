namespace Xiangqi.Server;

/// <summary>
/// Small multi-size (16/32 px, PNG-in-ICO) favicon served at /favicon.ico for browsers and crawlers that
/// request it directly. Embedded as base64 so the repository stays text-only; the SVG favicon remains the
/// primary one declared in index.html.
/// </summary>
internal static class FaviconIco
{
    private const string Base64 =
        "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";

    public static readonly byte[] Bytes = Convert.FromBase64String(Base64);
}
