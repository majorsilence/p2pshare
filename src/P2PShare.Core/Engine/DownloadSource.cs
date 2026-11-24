using System.Text.RegularExpressions;

namespace P2PShare.Core.Engine;

public enum DownloadSourceKind { Magnet, TorrentFile }

/// <summary>
/// What the user typed or picked when adding a download: a magnet link, a bare info-hash, or the
/// path of a .torrent file. Parsing is separated from the engine so it can be validated up front
/// (and tested) without touching the network.
/// </summary>
public sealed record DownloadSource(DownloadSourceKind Kind, string Value)
{
    private static readonly Regex HexInfoHash = new("^[0-9a-fA-F]{40}$", RegexOptions.Compiled);
    private static readonly Regex Base32InfoHash = new("^[A-Za-z2-7]{32}$", RegexOptions.Compiled);

    /// <exception cref="ArgumentException">The input is neither a magnet link, an info-hash, nor an existing file.</exception>
    public static DownloadSource Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Nothing to add - paste a magnet link or choose a .torrent file.");

        // Paths pasted from Explorer's "Copy as path" arrive wrapped in quotes.
        var text = input.Trim().Trim('"').Trim();

        if (text.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            return new DownloadSource(DownloadSourceKind.Magnet, text);

        if (HexInfoHash.IsMatch(text) || Base32InfoHash.IsMatch(text))
            return new DownloadSource(DownloadSourceKind.Magnet, $"magnet:?xt=urn:btih:{text}");

        if (File.Exists(text))
            return new DownloadSource(DownloadSourceKind.TorrentFile, Path.GetFullPath(text));

        throw new ArgumentException($"\"{Shorten(text)}\" is not a magnet link, an info-hash, or an existing .torrent file.");
    }

    private static string Shorten(string text) => text.Length <= 80 ? text : text[..77] + "...";
}
