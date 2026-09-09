using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Extras;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Extras
{
    [TestFixture]
    public class ExistingExtraFileEpisodeMatcherFixture : CoreTest
    {
        private List<EpisodeFile> _files;
        private List<EpisodeTrackFile> _links;
        private LocalEpisode _localEpisode;

        [SetUp]
        public void Setup()
        {
            _files = new List<EpisodeFile>
            {
                new EpisodeFile { Id = 10, RelativePath = "Series.S01E01.mkv" },
                new EpisodeFile { Id = 20, RelativePath = "Series.S01E01.2160p.mkv" }
            };
            _links = new List<EpisodeTrackFile>
            {
                new EpisodeTrackFile { EpisodeId = 1, TrackId = 1, EpisodeFileId = 10 },
                new EpisodeTrackFile { EpisodeId = 1, TrackId = 2, EpisodeFileId = 20 }
            };
            _localEpisode = new LocalEpisode
            {
                Series = new Series { Id = 1, Path = TempFolder },
                Episodes = new List<Episode> { new Episode { Id = 1, EpisodeFileId = 10 } }
            };
        }

        private EpisodeFile Match(string name, int? importedFileId = null)
        {
            _localEpisode.Path = Path.Combine(TempFolder, name);
            return new ExistingExtraFileEpisodeMatcher(_files, _links).Find(_localEpisode, importedFileId);
        }

        [TestCase("Series.S01E01.2160p.en.srt")]
        [TestCase("Series.S01E01.2160p-thumb.jpg")]
        [TestCase("Series.S01E01.2160p.nfo")]
        [TestCase("Series.S01E01.2160p_en_forced.srt")]
        public void should_choose_longest_matching_version_stem(string name)
        {
            Match(name).Id.Should().Be(20);
        }

        [Test]
        public void should_not_match_without_stem_boundary()
        {
            _files[0].RelativePath = "Series.S01E01.1080p.mkv";
            Match("Series.S01E01.2160pExtra.en.srt").Should().BeNull();
        }

        [Test]
        public void should_not_choose_primary_for_ambiguous_names()
        {
            _files[0].SceneName = "Original.S01E01";
            _files[1].SceneName = "Original.S01E01";
            Match("Original.S01E01.en.srt").Should().BeNull();
        }

        [Test]
        public void should_not_guess_owner_for_generic_name_with_multiple_versions()
        {
            Match("episode.en.srt").Should().BeNull();
        }

        [Test]
        public void should_preserve_generic_name_for_sole_physical_version()
        {
            _files.RemoveAt(1);
            Match("episode.en.srt").Id.Should().Be(10);
        }

        [Test]
        public void should_match_original_filename_after_rename()
        {
            _files[1].OriginalFilePath = Path.Combine("download", "Original.S01E01.2160p.mkv");
            Match("Original.S01E01.2160p.en.srt").Id.Should().Be(20);
        }

        [Test]
        public void should_preserve_dots_in_scene_name()
        {
            _files[1].SceneName = "Original.S01E01.2160p-GROUP";
            Match("Original.S01E01.2160p-GROUP.en.srt").Id.Should().Be(20);
        }

        [Test]
        public void should_require_matching_directory_when_multiple_versions_exist()
        {
            _files[1].RelativePath = Path.Combine("another", _files[1].RelativePath);
            _files[0].RelativePath = "Series.S01E01.1080p.mkv";
            Match("Series.S01E01.2160p.en.srt").Should().BeNull();
        }

        [Test]
        public void should_use_explicit_import_owner_after_script_rename()
        {
            Match("Old.Script.Name.en.srt", 20).Id.Should().Be(20);
        }

        [Test]
        public void should_not_fallback_when_explicit_owner_no_longer_matches_episodes()
        {
            Match("Series.S01E01.en.srt", 30).Should().BeNull();
        }

        [Test]
        public void should_deduplicate_shared_track_links()
        {
            _links.Add(new EpisodeTrackFile { EpisodeId = 1, TrackId = 3, EpisodeFileId = 20 });
            Match("Series.S01E01.2160p.en.srt").Id.Should().Be(20);
        }

        [Test]
        public void should_ignore_unlinked_file_even_when_its_filename_is_a_longer_match()
        {
            _files.Add(new EpisodeFile { Id = 30, RelativePath = "Series.S01E01.2160p.en.mkv" });
            Match("Series.S01E01.2160p.en.srt").Id.Should().Be(20);
        }

        [Test]
        public void should_not_assign_unparsed_sidecar()
        {
            _localEpisode.Episodes.Clear();
            Match("Series.S01E01.2160p.en.srt").Should().BeNull();
        }

        [Test]
        public void should_require_owner_to_cover_every_parsed_episode()
        {
            _localEpisode.Episodes.Add(new Episode { Id = 2, EpisodeFileId = 10 });
            _links.Add(new EpisodeTrackFile { EpisodeId = 2, TrackId = 1, EpisodeFileId = 10 });
            Match("generic.srt").Id.Should().Be(10);
        }
    }
}
