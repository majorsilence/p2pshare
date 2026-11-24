using P2PShare.Core.Engine;

namespace P2PShare.Core.Tests;

public class DownloadSourceTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void Magnet_links_are_accepted_as_is()
    {
        var magnet = $"magnet:?xt=urn:btih:{Hash}&dn=Example";
        var source = DownloadSource.Parse($"  {magnet}  ");

        Assert.Equal(DownloadSourceKind.Magnet, source.Kind);
        Assert.Equal(magnet, source.Value);
    }

    [Fact]
    public void Magnet_scheme_is_case_insensitive()
    {
        Assert.Equal(DownloadSourceKind.Magnet, DownloadSource.Parse($"MAGNET:?xt=urn:btih:{Hash}").Kind);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef01234567")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567")] // 32-char base32
    public void Bare_info_hashes_become_magnet_links(string hash)
    {
        var source = DownloadSource.Parse(hash);

        Assert.Equal(DownloadSourceKind.Magnet, source.Kind);
        Assert.Equal($"magnet:?xt=urn:btih:{hash}", source.Value);
    }

    [Fact]
    public void An_existing_file_is_a_torrent_file_even_when_quoted()
    {
        using var dir = new TempDir();
        var path = dir.File("example.torrent");
        File.WriteAllText(path, "not really a torrent; parsing happens later");

        var source = DownloadSource.Parse($"\"{path}\"");

        Assert.Equal(DownloadSourceKind.TorrentFile, source.Kind);
        Assert.Equal(Path.GetFullPath(path), source.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_input_is_rejected(string? input)
    {
        Assert.Throws<ArgumentException>(() => DownloadSource.Parse(input));
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("https://example.com/file.torrent")]
    [InlineData("C:\\definitely\\not\\here.torrent")]
    [InlineData("0123456789abcdef")] // too short for a hash
    public void Things_that_are_not_downloadable_are_rejected_with_a_helpful_message(string input)
    {
        var ex = Assert.Throws<ArgumentException>(() => DownloadSource.Parse(input));
        Assert.Contains("not a magnet link", ex.Message);
    }

    [Fact]
    public void Very_long_garbage_is_shortened_in_the_error_message()
    {
        var ex = Assert.Throws<ArgumentException>(() => DownloadSource.Parse(new string('x', 5000)));
        Assert.True(ex.Message.Length < 300);
    }
}
