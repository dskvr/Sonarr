using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Extras;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.Extras.Subtitles;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.EpisodeImport.Aggregation;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Extras
{
    [TestFixture]
    public class ExistingVersionSidecarsFixture : CoreTest
    {
        private Series _series;
        private List<MetadataFile> _metadata;
        private List<OtherExtraFile> _otherFiles;
        private List<SubtitleFile> _subtitles;

        [SetUp]
        public void Setup()
        {
            _metadata = new List<MetadataFile>();
            _otherFiles = new List<OtherExtraFile>();
            _subtitles = new List<SubtitleFile>();
            _series = new Series { Id = 1, Path = TempFolder };
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFilesBySeries(1)).Returns(new List<EpisodeFile>
            {
                new EpisodeFile { Id = 10, RelativePath = "Series.S01E01.1080p.mkv" },
                new EpisodeFile { Id = 20, RelativePath = "Series.S01E01.2160p.mkv" }
            });
            Mocker.GetMock<IEpisodeTrackFileService>().Setup(s => s.GetForSeries(1)).Returns(new List<EpisodeTrackFile>
            {
                new EpisodeTrackFile { EpisodeId = 1, TrackId = 1, EpisodeFileId = 10 },
                new EpisodeTrackFile { EpisodeId = 1, TrackId = 2, EpisodeFileId = 20 }
            });
            Mocker.GetMock<IAggregationService>().Setup(s => s.Augment(It.IsAny<LocalEpisode>(), null))
                .Callback<LocalEpisode, DownloadClientItem>((local, _) => local.Episodes = new List<Episode>
                {
                    new Episode { Id = 1, EpisodeFileId = 10 }
                });
            Mocker.GetMock<IExtraFileService<MetadataFile>>().Setup(s => s.GetFilesBySeries(1)).Returns(() => _metadata.ToList());
            Mocker.GetMock<IExtraFileService<MetadataFile>>().Setup(s => s.Upsert(It.IsAny<List<MetadataFile>>())).Callback<List<MetadataFile>>(files => _metadata.AddRange(files));
            Mocker.GetMock<IExtraFileService<OtherExtraFile>>().Setup(s => s.GetFilesBySeries(1)).Returns(() => _otherFiles.ToList());
            Mocker.GetMock<IExtraFileService<OtherExtraFile>>().Setup(s => s.Upsert(It.IsAny<List<OtherExtraFile>>())).Callback<List<OtherExtraFile>>(files => _otherFiles.AddRange(files));
            Mocker.GetMock<IExtraFileService<OtherExtraFile>>().Setup(s => s.DeleteMany(It.IsAny<IEnumerable<int>>())).Callback<IEnumerable<int>>(ids =>
            {
                var removedIds = ids.ToHashSet();
                _otherFiles.RemoveAll(f => removedIds.Contains(f.Id));
            });
            Mocker.GetMock<IExtraFileService<SubtitleFile>>().Setup(s => s.GetFilesBySeries(1)).Returns(() => _subtitles.ToList());
            Mocker.GetMock<IExtraFileService<SubtitleFile>>().Setup(s => s.Upsert(It.IsAny<List<SubtitleFile>>())).Callback<List<SubtitleFile>>(files => _subtitles.AddRange(files));
            var consumer = new Mock<IMetadata>();
            consumer.Setup(c => c.FindMetadataFile(_series, It.IsAny<string>())).Returns((Series series, string path) =>
            {
                if (!path.EndsWith("nfo") && !path.EndsWith("jpg"))
                {
                    return null;
                }

                return new MetadataFile
                {
                    SeriesId = series.Id,
                    RelativePath = Path.GetFileName(path),
                    Type = path.EndsWith("jpg") ? MetadataType.EpisodeImage : MetadataType.EpisodeMetadata
                };
            });
            Mocker.SetConstant<IEnumerable<IMetadata>>(new[] { consumer.Object });
        }

        private ExistingExtraFileService GetImportService()
        {
            Mocker.SetConstant<IEnumerable<IImportExistingExtraFiles>>(new IImportExistingExtraFiles[]
            {
                Mocker.Resolve<ExistingOtherExtraImporter>(),
                Mocker.Resolve<ExistingSubtitleImporter>(),
                Mocker.Resolve<ExistingMetadataImporter>()
            });
            return Mocker.Resolve<ExistingExtraFileService>();
        }

        [Test]
        public void should_import_script_sidecars_through_ordered_pipeline_for_explicit_owner()
        {
            var paths = new[] { "Old.Script.Name.nfo", "Old.Script.Name.en.srt", "Old.Script.Name.extra" }
                .Select(name => Path.Combine(TempFolder, name)).ToList();

            var result = GetImportService().ImportExtraFiles(_series, paths, "Old.Script.Name.mkv", 20);

            result.Should().BeEquivalentTo(paths);
            _metadata.Should().ContainSingle().Which.EpisodeFileId.Should().Be(20);
            _subtitles.Should().ContainSingle().Which.EpisodeFileId.Should().Be(20);
            _otherFiles.Should().ContainSingle().Which.EpisodeFileId.Should().Be(20);
        }

        [Test]
        public void should_preserve_known_secondary_ownership_and_remove_only_missing_sidecar_entries_on_scan()
        {
            _otherFiles.AddRange(new[]
            {
                new OtherExtraFile { Id = 1, EpisodeFileId = 20, RelativePath = "generic.txt" },
                new OtherExtraFile { Id = 2, EpisodeFileId = 10, RelativePath = "missing.txt" }
            });
            var paths = new[] { "generic.txt", "Series.S01E01.2160p.extra" }
                .Select(name => Path.Combine(TempFolder, name)).ToList();

            GetImportService().Handle(new SeriesScannedEvent(_series, paths));

            _otherFiles.Should().HaveCount(2).And.OnlyContain(f => f.EpisodeFileId == 20);
            _otherFiles.Should().ContainSingle(f => f.Id == 1 && f.RelativePath == "generic.txt");
            _otherFiles.Should().NotContain(f => f.Id == 2);
        }

        [Test]
        public void should_not_assign_ambiguous_sidecars_when_pipeline_has_no_explicit_owner()
        {
            var paths = new[] { "Series.S01E01.nfo", "Series.S01E01.en.srt", "Series.S01E01.txt" }
                .Select(name => Path.Combine(TempFolder, name)).ToList();

            GetImportService().ImportExtraFiles(_series, paths, null).Should().BeEmpty();

            _metadata.Should().BeEmpty();
            _subtitles.Should().BeEmpty();
            _otherFiles.Should().BeEmpty();
        }

        [Test]
        public void should_skip_other_extra_when_episode_aggregation_cannot_identify_owner()
        {
            Mocker.GetMock<IAggregationService>().Setup(s => s.Augment(It.IsAny<LocalEpisode>(), null))
                .Callback<LocalEpisode, DownloadClientItem>((local, _) => local.Episodes = new List<Episode>());

            var result = Mocker.Resolve<ExistingOtherExtraImporter>().ProcessFiles(_series, new List<string> { Path.Combine(TempFolder, "Series.S01E01.2160p.extra") }, new List<string>(), null);

            result.Should().BeEmpty();
            _otherFiles.Should().BeEmpty();
        }

        [TestCase("Series.S01E01.2160p.nfo")]
        [TestCase("Series.S01E01.2160p-thumb.jpg")]
        public void should_attach_metadata_to_secondary_physical_file(string filename)
        {
            var result = Mocker.Resolve<ExistingMetadataImporter>().ProcessFiles(_series, new List<string> { Path.Combine(TempFolder, filename) }, new List<string>(), null);

            result.Should().ContainSingle().Which.EpisodeFileId.Should().Be(20);
        }

        [Test]
        public void should_attach_other_extra_to_secondary_physical_file()
        {
            var result = Mocker.Resolve<ExistingOtherExtraImporter>().ProcessFiles(_series, new List<string> { Path.Combine(TempFolder, "Series.S01E01.2160p.extra") }, new List<string>(), null);

            result.Should().ContainSingle().Which.EpisodeFileId.Should().Be(20);
        }

        [Test]
        public void should_leave_ambiguous_metadata_unassigned()
        {
            var result = Mocker.Resolve<ExistingMetadataImporter>().ProcessFiles(_series, new List<string> { Path.Combine(TempFolder, "Series.S01E01.nfo") }, new List<string>(), null);

            result.Should().BeEmpty();
        }
    }
}
