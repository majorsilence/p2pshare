using P2PShare.Core.Search;

namespace P2PShare.Core.Tests;

public class ContentClassifierTests
{
    private const long MB = 1024 * 1024;

    private static ContentCategory Classify(string name, params (string, long)[] files) =>
        ContentClassifier.Classify(name, files);

    [Theory]
    [InlineData("Big.Buck.Bunny.1080p.mkv", "Big.Buck.Bunny.1080p.mkv", ContentCategory.Video)]
    [InlineData("Holiday film 1920x1080", "film.mp4", ContentCategory.Video)] // a resolution is not an episode number
    [InlineData("Some.Show.S02E05.720p", "ep.mkv", ContentCategory.TV)]
    [InlineData("Some Show Season 3", "e1.mkv", ContentCategory.TV)]
    [InlineData("Some Show 3x07", "show.avi", ContentCategory.TV)]
    [InlineData("Artist - Album (2020) [FLAC]", "01 song.flac", ContentCategory.Music)]
    [InlineData("A Long Story Audiobook", "part1.m4b", ContentCategory.Books)]
    [InlineData("A Novel", "novel.epub", ContentCategory.Books)]
    [InlineData("Java Programming Guide", "java.pdf", ContentCategory.Books)]
    [InlineData("ubuntu-24.04-desktop-amd64.iso", "ubuntu-24.04-desktop-amd64.iso", ContentCategory.Software)]
    [InlineData("Old.Movie.DVDRip", "disc.iso", ContentCategory.Video)]
    [InlineData("Photo Tool v2.1", "setup.msi", ContentCategory.Software)]
    [InlineData("Space Quest [FitGirl Repack]", "setup.exe", ContentCategory.Games)]
    [InlineData("Platformer [NSP]", "platformer.nsp", ContentCategory.Games)]
    [InlineData("Vacation photos", "img1.jpg", ContentCategory.Images)]
    [InlineData("Something.2019.1080p.BluRay", "something.rar", ContentCategory.Video)]
    [InlineData("Some Albums Discography", "all.zip", ContentCategory.Music)]
    [InlineData("backup", "backup.7z", ContentCategory.Other)]
    [InlineData("mystery", "data.xyz", ContentCategory.Other)]
    public void Typical_torrents_land_in_the_expected_category(string name, string file, ContentCategory expected) =>
        Assert.Equal(expected, Classify(name, (file, 700 * MB)));

    [Fact]
    public void The_files_holding_most_bytes_decide_not_the_extras()
    {
        Assert.Equal(ContentCategory.Video, Classify("A Film",
            ("film.mkv", 1500 * MB), ("cover.jpg", 1 * MB), ("english.srt", 60_000), ("info.nfo", 2_000), ("sample.rar", 5 * MB)));
        Assert.Equal(ContentCategory.Music, Classify("An Album",
            ("1.mp3", 8 * MB), ("2.mp3", 9 * MB), ("booklet.pdf", 3 * MB), ("front.png", 2 * MB)));
    }

    [Fact]
    public void A_split_archive_is_judged_by_its_name()
    {
        Assert.Equal(ContentCategory.TV, Classify("Show.S01E02.1080p.WEB-DL",
            ("a.r00", 100 * MB), ("a.r01", 100 * MB), ("a.rar", 100 * MB)));
    }

    [Fact]
    public void With_no_recognisable_files_the_name_is_used()
    {
        Assert.Equal(ContentCategory.Books, Classify("Collected ebook library"));
        Assert.Equal(ContentCategory.Other, Classify("zzz"));
    }

    [Fact]
    public void A_dot_in_a_folder_name_is_not_an_extension() =>
        Assert.Equal(ContentCategory.Other, Classify("thing", ("release.v2/readme", 10)));

    [Fact]
    public void Files_reported_as_empty_still_count() =>
        Assert.Equal(ContentCategory.Music, Classify("x", ("a.mp3", 0)));

    [Theory]
    [InlineData("Late Night XXX Compilation", "clip.mp4")]
    [InlineData("holiday", "PORN/clip.mp4")] // spotted from a file path inside
    [InlineData("Some.JAV.Release", "a.mp4")]
    public void Adult_markers_win_over_the_file_type(string name, string file) =>
        Assert.Equal(ContentCategory.Adult, Classify(name, (file, 700 * MB)));

    [Theory]
    [InlineData("xXx State of the Union 2005 1080p", ContentCategory.Video)]
    [InlineData("Adult Swim Collection S01", ContentCategory.TV)]
    [InlineData("Essex Wildlife", ContentCategory.Video)]
    public void Look_alike_words_are_not_treated_as_adult(string name, ContentCategory expected) =>
        Assert.Equal(expected, Classify(name, ("a.mkv", 700 * MB)));

    // The shapes below were misfiled when the first version was run over a real index.

    [Theory]
    [InlineData("[SubsPlease] Some Anime Title - 05 (720p) [6EC1B642].mkv")]
    [InlineData("[ASW] Another Show - 02 [1080p HEVC][E353CF45].mkv")]
    [InlineData("Una Serie [HDTV][Cap.104]")]
    public void Other_ways_of_naming_an_episode_are_recognised(string name) =>
        Assert.Equal(ContentCategory.TV, Classify(name, ("a.mkv", 300 * MB)));

    [Fact]
    public void A_folder_of_numbered_videos_is_a_series()
    {
        var files = Enumerable.Range(1, 12)
            .Select(i => ($"Collection/[Group][{i}] Some Arc {i:00} [1080p][ABCD].mkv", 400 * MB))
            .ToArray();

        Assert.Equal(ContentCategory.TV, ContentClassifier.Classify("Long Running Show", files));
        // Two parts of one film are not a series.
        Assert.Equal(ContentCategory.Video, Classify("A Film", ("A Film part 1.mkv", MB), ("A Film part 2.mkv", MB)));
    }

    [Fact]
    public void A_game_repack_with_big_data_files_is_a_game_even_with_a_soundtrack() =>
        Assert.Equal(ContentCategory.Games, Classify("[R.G. Mechanics] Racer",
            ("data1.bin", 3000 * MB), ("music.mp3", 200 * MB), ("Redist/driver.msi", 20 * MB)));

    [Theory]
    [InlineData("ABCD-123")]
    [InlineData("WXYZ-658-U")]
    [InlineData("Someone & Someone 4on2 scene")]
    [InlineData("clip collection 18+")]
    [InlineData("成人 合集")]
    public void Further_adult_naming_styles_are_recognised(string name) =>
        Assert.Equal(ContentCategory.Adult, Classify(name, ("a.mp4", 700 * MB)));

    [Theory]
    [InlineData("ABC-123 firmware", "update.exe", ContentCategory.Software)] // not a bare code
    [InlineData("ABCD-123", "manual.pdf", ContentCategory.Books)]            // codes only count for video
    [InlineData("Top 2018 Hits", "a.mp3", ContentCategory.Music)]            // "18" inside a year
    public void Similar_looking_names_are_left_alone(string name, string file, ContentCategory expected) =>
        Assert.Equal(expected, Classify(name, (file, 10 * MB)));

    [Fact]
    public void Hostile_input_is_handled()
    {
        var name = new string('a', 100_000) + " s01e01";
        var files = Enumerable.Range(0, 5_000).Select(i => (new string('.', 500) + i + ".mkv", 1L)).ToArray();

        var category = ContentClassifier.Classify(name, files);

        Assert.True(Enum.IsDefined(category));
        Assert.Equal(ContentCategory.Other, ContentClassifier.Classify("", Array.Empty<(string, long)>()));
    }
}
