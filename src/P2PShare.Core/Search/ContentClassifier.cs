using System.Text.RegularExpressions;

namespace P2PShare.Core.Search;

/// <summary>What a torrent appears to contain.</summary>
public enum ContentCategory
{
    Video,
    TV,
    Music,
    Books,
    Software,
    Games,
    Images,
    Adult,
    Other,
}

/// <summary>
/// Guesses a torrent's category from what its metadata says: mostly which kind of file holds most of
/// the bytes, with the name as a tie-breaker (is that video a TV episode? is that installer a game?).
/// It is a heuristic over strings written by strangers - good enough to filter a result list, and
/// not a safety filter.
/// </summary>
public static class ContentClassifier
{
    /// <summary>Bump when the rules change, so that an existing index is labelled again.</summary>
    public const int Version = 1;

    private enum Kind { Video, Audio, Book, Program, DiskImage, Archive, Image, GameFile }

    private const int MaxFilesExamined = 2000;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly Dictionary<string, Kind> Extensions = Build(
        (Kind.Video, "mkv mp4 avi wmv mov m4v mpg mpeg ts m2ts vob webm flv ogm ogv divx 3gp rmvb"),
        (Kind.Audio, "mp3 flac m4a m4b aac ogg opus wav wma alac ape mka aiff dsf dff"),
        (Kind.Book, "epub mobi azw azw3 pdf djvu cbz cbr cb7 fb2 lit chm doc docx"),
        (Kind.Program, "exe msi msix dmg pkg apk deb rpm appimage ipa jar"),
        (Kind.DiskImage, "iso img nrg mdf cue bin"),
        (Kind.Archive, "zip rar 7z tar gz bz2 xz zst cab"),
        (Kind.Image, "jpg jpeg png gif bmp webp tif tiff psd raw cr2 nef heic"),
        (Kind.GameFile, "nsp xci nsz wbfs gcm cia 3ds nds gba rvz chd pbp"));

    // Split archives: .r00-.r99, .z01, .001
    private static readonly Regex ArchivePart = New(@"^(r\d{2}|z\d{2}|\d{3})$");

    // Runs of punctuation and spaces collapse to one space, so "[R.G. Mechanics]" reads "r g mechanics".
    private static readonly Regex Separators = New(@"[\s\._\-\[\]\(\)\{\}\+]+");

    private static readonly Regex AdultMarkers = New(
        @"\b(xxx|porn|porno|pornography|hentai|erotica|adult video|onlyfans|brazzers|jav|anal|blowjob|creampie|" +
        @"gangbang|bukkake|milf|[2-9]on[1-9]|pornhub|legalporno|xchina|bangbros|naughtyamerica|realitykings|evilangel|tushy)\b");

    // Markers that word boundaries and separator clean-up would miss; matched against the raw text.
    private static readonly Regex RawAdultMarkers = New(@"(?<!\d)18\+|18禁|成人|無修正|无码|無碼|色情|里番|裏番");

    // A bare release code such as "ABCD-123" is how a large family of adult videos is named.
    private static readonly Regex ReleaseCode = New(@"^[a-z]{3,6}-\d{3,5}(-[a-z]{1,2})?(\.[a-z0-9]{2,4})?$");

    // A well-known action film whose title would otherwise trip the first marker above.
    private static readonly Regex NotAdult = New(@"\bxxx (2002|state of the union|return of xander)");

    private static readonly Regex Episode = New(
        @"\b(s\d{1,2} ?e\d{1,3}|\d{1,2}x\d{2}|season ?\d{1,2}|temporada ?\d{1,2}|cap ?\d{2,4}|s\d{2}|episode ?\d{1,3}|complete series)\b");

    // Fan-subtitled releases: "[Group] Title - 05 (720p)". Matched against the raw text.
    private static readonly Regex FansubEpisode = New(@"^\[[^\]]{1,40}\].*\s-\s\d{1,3}(v\d)?\s*([\[\(]|\.[a-z0-9]{2,4}$)");

    private static readonly Regex Tags = New(@"\[[^\]]*\]|\([^\)]*\)");
    private static readonly Regex TrailingNumber = New(@"(?:^|[\s_\-\.])(\d{1,3})(v\d)?\s*$");

    /// <summary>This many differently numbered videos in one torrent reads as a series.</summary>
    private const int NumberedFilesForSeries = 6;

    private static readonly Regex GameWords = New(
        @"\b(repack|fitgirl|dodi|gog|codex|skidrow|razor1911|tenoke|steamrip|elamigos|goldberg|xatab|r g mechanics|r g catalyst|nsp|xci|nintendo|switch|ps[2345]|xbox|game)\b");

    private static readonly Regex VideoWords = New(
        @"\b(480p|576p|720p|1080p|1080i|2160p|4k|x264|x265|h ?264|h ?265|hevc|avc|bluray|blu ray|web ?dl|webrip|hdtv|dvdrip|brrip|bdrip|dvd[59r]?|camrip|hdrip)\b");

    private static readonly Regex MusicWords = New(@"\b(flac|mp3|320 ?kbps|v0|discography|album|vinyl|soundtrack|ost)\b");
    private static readonly Regex BookWords = New(@"\b(epub|mobi|azw3|pdf|ebook|e book|magazine|comics?|audiobook)\b");

    private static readonly Regex SoftwareWords = New(
        @"\b(windows|win(32|64)|linux|ubuntu|debian|fedora|macos|mac os|x64|x86|amd64|setup|installer|portable|activator|keygen|crack(ed)?)\b");

    public static ContentCategory Classify(string name, IEnumerable<(string Path, long Length)> files)
    {
        var bytes = new Dictionary<Kind, long>();
        var title = Normalise(name);
        var rawName = Clip(name).Trim();
        var adult = IsAdult(title) || Matches(RawAdultMarkers, rawName);
        var episode = Matches(Episode, title) || Matches(FansubEpisode, rawName);
        var numberedVideos = new HashSet<string>();
        int videoFiles = 0;

        foreach (var (path, length) in files.Take(MaxFilesExamined))
        {
            var text = Normalise(path);
            var raw = Clip(path);
            var fileName = raw[(raw.LastIndexOfAny(new[] { '/', '\\' }) + 1)..];
            adult |= IsAdult(text) || Matches(RawAdultMarkers, raw);
            episode |= Matches(Episode, text) || Matches(FansubEpisode, fileName);

            var extension = ExtensionOf(path);
            if (Extensions.TryGetValue(extension, out var video) && video == Kind.Video)
            {
                videoFiles++;
                var number = TrailingNumberOf(fileName);
                if (number is not null) numberedVideos.Add(number);
            }

            Kind kind;
            if (Extensions.TryGetValue(extension, out var known)) kind = known;
            else if (Matches(ArchivePart, extension)) kind = Kind.Archive;
            else continue; // subtitles, .nfo, .txt and the unknown say nothing about the content

            // +1 so that files reported as empty still count for something.
            bytes[kind] = bytes.GetValueOrDefault(kind) + Math.Max(0, length) + 1;
        }

        Kind? dominant = bytes.Count == 0 ? null : bytes.MaxBy(pair => pair.Value).Key;

        // A bare release code only means something when the content is video.
        adult |= dominant == Kind.Video && Matches(ReleaseCode, rawName);
        if (adult) return ContentCategory.Adult;

        episode |= numberedVideos.Count >= NumberedFilesForSeries && numberedVideos.Count * 2 >= videoFiles;
        var audiobook = title.Contains("audiobook", StringComparison.Ordinal) || title.Contains("audio book", StringComparison.Ordinal);

        return dominant switch
        {
            Kind.Video => episode ? ContentCategory.TV : ContentCategory.Video,
            Kind.Audio => audiobook ? ContentCategory.Books : ContentCategory.Music,
            Kind.Book => ContentCategory.Books,
            Kind.Image => ContentCategory.Images,
            Kind.GameFile => ContentCategory.Games,
            Kind.Program => Matches(GameWords, title) ? ContentCategory.Games : ContentCategory.Software,
            // A disc image or archive is just a container; the name says what is in it.
            Kind.DiskImage => FromName(title, episode, otherwise: ContentCategory.Software),
            Kind.Archive => FromName(title, episode, otherwise: ContentCategory.Other),
            _ => FromName(title, episode, otherwise: ContentCategory.Other),
        };
    }

    private static ContentCategory FromName(string title, bool episode, ContentCategory otherwise)
    {
        if (Matches(VideoWords, title)) return episode ? ContentCategory.TV : ContentCategory.Video;
        if (Matches(BookWords, title)) return ContentCategory.Books;
        if (Matches(MusicWords, title)) return ContentCategory.Music;
        if (Matches(GameWords, title)) return ContentCategory.Games;
        if (Matches(SoftwareWords, title)) return ContentCategory.Software;
        return otherwise;
    }

    private static bool IsAdult(string text) => Matches(AdultMarkers, text) && !Matches(NotAdult, text);

    private static string Clip(string text) => text.Length > 600 ? text[..600] : text;

    private static string Normalise(string text) => Separators.Replace(Clip(text), " ").ToLowerInvariant();

    /// <summary>The number a file name ends with once its tags and extension are set aside, if any.</summary>
    private static string? TrailingNumberOf(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        var stem = Tags.Replace(dot > 0 ? fileName[..dot] : fileName, " ");
        try
        {
            var match = TrailingNumber.Match(stem);
            // The words before the number keep "Arc A 01" and "Arc B 01" apart.
            return match.Success ? stem.Trim().ToLowerInvariant() : null;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }

    private static string ExtensionOf(string path)
    {
        var dot = path.LastIndexOf('.');
        return dot < 0 || dot < path.LastIndexOfAny(new[] { '/', '\\' }) ? "" : path[(dot + 1)..].ToLowerInvariant();
    }

    private static bool Matches(Regex regex, string text)
    {
        try { return regex.IsMatch(text); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static Regex New(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    private static Dictionary<string, Kind> Build(params (Kind Kind, string Extensions)[] groups) =>
        groups.SelectMany(g => g.Extensions.Split(' ').Select(e => (Extension: e, g.Kind)))
              .ToDictionary(x => x.Extension, x => x.Kind, StringComparer.OrdinalIgnoreCase);
}
