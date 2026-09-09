using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Subtitles;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.EpisodeImport.Aggregation;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Extras.Subtitles
{
    [TestFixture]
    public class ExistingSubtitleImporterFixture : CoreTest<ExistingSubtitleImporter>
    {
        private Series _series;
        private List<EpisodeFile> _files;

        [SetUp]
        public void Setup()
        {
            _series = new Series { Id = 1, Path = TempFolder };
            _files = new List<EpisodeFile>
            {
                new EpisodeFile { Id = 10, RelativePath = "Series.S01E01.1080p.mkv" },
                new EpisodeFile { Id = 20, RelativePath = "Series.S01E01.2160p.mkv" }
            };
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFilesBySeries(1)).Returns(_files);
            Mocker.GetMock<IEpisodeTrackFileService>().Setup(s => s.GetForSeries(1)).Returns(new List<EpisodeTrackFile>
            {
                new EpisodeTrackFile { EpisodeId = 1, TrackId = 1, EpisodeFileId = 10 },
                new EpisodeTrackFile { EpisodeId = 1, TrackId = 2, EpisodeFileId = 20 }
            });
            Mocker.GetMock<IExtraFileService<SubtitleFile>>().Setup(s => s.GetFilesBySeries(1)).Returns(new List<SubtitleFile>());
            Mocker.GetMock<IAggregationService>().Setup(s => s.Augment(It.IsAny<LocalEpisode>(), null))
                .Callback<LocalEpisode, DownloadClientItem>((local, _) => local.Episodes = new List<Episode>
                {
                    new Episode { Id = 1, EpisodeFileId = 10 }
                });
        }

        [Test]
        public void should_preserve_secondary_subtitle_when_primary_file_is_deleted()
        {
            Directory.CreateDirectory(TempFolder);
            var primaryPath = Path.Combine(TempFolder, "Series.S01E01.1080p.en.srt");
            var secondaryPath = Path.Combine(TempFolder, "Series.S01E01.2160p.en.srt");
            File.WriteAllText(primaryPath, "primary subtitle");
            File.WriteAllText(secondaryPath, "secondary subtitle");

            var imported = Subject.ProcessFiles(_series, new List<string> { primaryPath, secondaryPath }, new List<string>(), null).Cast<SubtitleFile>().ToList();
            imported.Single(s => s.RelativePath.Contains("2160p")).EpisodeFileId.Should().Be(20);
            Mocker.GetMock<IExtraFileRepository<SubtitleFile>>().Setup(s => s.GetFilesByEpisodeFile(It.IsAny<int>()))
                .Returns((int id) => imported.Where(s => s.EpisodeFileId == id).ToList());
            Mocker.GetMock<IExtraFileRepository<SubtitleFile>>().Setup(s => s.DeleteForEpisodeFile(It.IsAny<int>()))
                .Callback<int>(id => imported.RemoveAll(s => s.EpisodeFileId == id));
            Mocker.GetMock<ISeriesService>().Setup(s => s.GetSeries(1)).Returns(_series);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FileExists(It.IsAny<string>())).Returns((string path) => File.Exists(path));
            Mocker.GetMock<IDiskProvider>().Setup(s => s.GetParentFolder(It.IsAny<string>())).Returns((string path) => Path.GetDirectoryName(path));
            Mocker.GetMock<IRecycleBinProvider>().Setup(s => s.DeleteFile(It.IsAny<string>(), It.IsAny<string>()))
                .Callback<string, string>((path, _) => File.Delete(path));
            _files[0].SeriesId = 1;

            Mocker.Resolve<SubtitleFileService>().Handle(new EpisodeFileDeletedEvent(_files[0], DeleteMediaFileReason.Manual));

            File.Exists(primaryPath).Should().BeFalse();
            File.ReadAllText(secondaryPath).Should().Be("secondary subtitle");
            imported.Should().ContainSingle().Which.EpisodeFileId.Should().Be(20);
            Mocker.GetMock<IEpisodeTrackFileService>().Verify(s => s.GetForSeries(1), Times.Once());
        }

        [Test]
        public void should_skip_ambiguous_subtitle_without_primary_fallback()
        {
            var result = Subject.ProcessFiles(_series, new List<string> { Path.Combine(TempFolder, "Series.S01E01.en.srt") }, new List<string>(), null);
            result.Should().BeEmpty();
        }

        [Test]
        public void should_use_explicit_owner_for_script_import_with_old_filename()
        {
            var result = Subject.ProcessFiles(_series, new List<string> { Path.Combine(TempFolder, "Series.S01E01.old.en.srt") }, new List<string>(), "Series.S01E01.old.mkv", 20);
            result.Should().ContainSingle().Which.EpisodeFileId.Should().Be(20);
        }
    }
}
