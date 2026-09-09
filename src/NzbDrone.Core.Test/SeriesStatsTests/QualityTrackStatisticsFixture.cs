using System;
using System.Collections.Generic;
using System.Linq;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.SeriesStats;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.SeriesStatsTests
{
    [TestFixture]
    public class QualityTrackStatisticsFixture : CoreTest<SeriesStatisticsService>
    {
        private List<SeriesQualityTrack> _tracks;
        private List<Episode> _episodes;
        private List<EpisodeTrackFile> _links;
        private List<QualityProfile> _profiles;

        [SetUp]
        public void Setup()
        {
            _tracks = new List<SeriesQualityTrack>
            {
                new SeriesQualityTrack { Id = 10, SeriesId = 1, QualityProfileId = 1, IsPrimary = true, Enabled = true },
                new SeriesQualityTrack { Id = 20, SeriesId = 1, QualityProfileId = 2, Enabled = true },
                new SeriesQualityTrack { Id = 30, SeriesId = 1, QualityProfileId = 3, Enabled = false }
            };
            _profiles = _tracks.Select(t => new QualityProfile { Id = t.QualityProfileId }).ToList();
            _episodes = new List<Episode>
            {
                new Episode { Id = 1, SeriesId = 1, SeasonNumber = 1, Monitored = true, AirDateUtc = DateTime.UtcNow.AddDays(-1) },
                new Episode { Id = 2, SeriesId = 1, SeasonNumber = 1, Monitored = true, AirDateUtc = DateTime.UtcNow.AddDays(-1) },
                new Episode { Id = 3, SeriesId = 1, SeasonNumber = 2, Monitored = true, AirDateUtc = DateTime.UtcNow.AddDays(1) }
            };
            _links = new List<EpisodeTrackFile>
            {
                new EpisodeTrackFile { EpisodeId = 1, TrackId = 10, EpisodeFileId = 1 },
                new EpisodeTrackFile { EpisodeId = 2, TrackId = 10, EpisodeFileId = 1 },
                new EpisodeTrackFile { EpisodeId = 1, TrackId = 20, EpisodeFileId = 1 },
                new EpisodeTrackFile { EpisodeId = 2, TrackId = 30, EpisodeFileId = 1 }
            };
            var files = new List<EpisodeFile> { new EpisodeFile { Id = 1, SeriesId = 1, Quality = new QualityModel(Quality.HDTV1080p), Size = 100 } };

            Mocker.GetMock<ISeriesStatisticsRepository>().Setup(s => s.SeriesStatistics(1)).Returns(() => new List<SeasonStatistics>
            {
                new SeasonStatistics { SeriesId = 1, SeasonNumber = 1, EpisodeCount = 2, EpisodeFileCount = 2, SizeOnDisk = 100 },
                new SeasonStatistics { SeriesId = 1, SeasonNumber = 2 }
            });
            Mocker.GetMock<IQualityProfileService>().Setup(s => s.Get(1)).Returns(_profiles[0]);
            Mocker.GetMock<IQualityProfileService>().Setup(s => s.All()).Returns(_profiles);
            Mocker.GetMock<ISeriesQualityTrackService>().Setup(s => s.GetEnabledTracks(1)).Returns(() => _tracks.Where(t => t.Enabled).ToList());
            Mocker.GetMock<IEpisodeService>().Setup(s => s.GetEpisodeBySeries(1)).Returns(_episodes);
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFilesBySeries(1)).Returns(files);
            Mocker.GetMock<IEpisodeTrackFileService>().Setup(s => s.GetForSeries(1)).Returns(_links);
            Mocker.GetMock<ICustomFormatCalculationService>().Setup(s => s.ParseCustomFormat(It.IsAny<EpisodeFile>())).Returns(new List<CustomFormat>());
            Mocker.GetMock<IUpgradableSpecification>()
                .Setup(s => s.CutoffNotMet(_profiles[1], It.IsAny<QualityModel>(), It.IsAny<List<CustomFormat>>(), null))
                .Returns(true);
        }

        [Test]
        public void should_report_missing_and_cutoff_per_enabled_track_without_counting_shared_bytes_twice()
        {
            var statistics = Subject.SeriesStatistics(1, 1);
            var primary = statistics.QualityTracks.Single(t => t.TrackId == 10);
            var additional = statistics.QualityTracks.Single(t => t.TrackId == 20);

            Assert.That(statistics.QualityTracks, Has.Count.EqualTo(2));
            Assert.That(primary.EpisodeCount, Is.EqualTo(2));
            Assert.That(primary.EpisodeFileCount, Is.EqualTo(2));
            Assert.That(primary.MissingCount, Is.Zero);
            Assert.That(primary.CutoffUnmetCount, Is.Zero);
            Assert.That(additional.EpisodeCount, Is.EqualTo(2));
            Assert.That(additional.EpisodeFileCount, Is.EqualTo(1));
            Assert.That(additional.MissingCount, Is.EqualTo(1));
            Assert.That(additional.CutoffUnmetCount, Is.EqualTo(1));
            Assert.That(statistics.SizeOnDisk, Is.EqualTo(100));
            Assert.That(statistics.EpisodeCount, Is.EqualTo(2));
            Assert.That(statistics.SeasonStatistics.Single(s => s.SeasonNumber == 2).QualityTracks.All(t => t.EpisodeCount == 0), Is.True);
            Mocker.GetMock<ICustomFormatCalculationService>().Verify(s => s.ParseCustomFormat(It.IsAny<EpisodeFile>()), Times.Once());
        }

        [Test]
        public void should_include_track_completion_in_series_index_statistics()
        {
            Mocker.GetMock<ISeriesStatisticsRepository>()
                .Setup(s => s.SeriesStatistics())
                .Returns(new List<SeasonStatistics> { new SeasonStatistics { SeriesId = 1, SeasonNumber = 1, EpisodeCount = 2, EpisodeFileCount = 2, SizeOnDisk = 100 } });
            Mocker.GetMock<ISeriesService>().Setup(s => s.GetAllSeriesQualityProfiles()).Returns(new Dictionary<int, int> { [1] = 1 });
            Mocker.GetMock<ISeriesQualityTrackService>().Setup(s => s.GetAllTracks()).Returns(_tracks);

            var statistics = Subject.SeriesStatistics().Single();

            Assert.That(statistics.QualityTracks.Select(t => t.TrackId), Is.EquivalentTo(new[] { 10, 20 }));
            Assert.That(statistics.QualityTracks.Single(t => t.TrackId == 20).MissingCount, Is.EqualTo(1));
            Assert.That(statistics.SizeOnDisk, Is.EqualTo(100));
        }

        [Test]
        public void should_preserve_single_profile_statistics_without_loading_extra_files()
        {
            _tracks[1].Enabled = false;

            var statistics = Subject.SeriesStatistics(1, 1);

            Assert.That(statistics.QualityTracks, Is.Empty);
            Assert.That(statistics.EpisodeFileCount, Is.EqualTo(2));
            Assert.That(statistics.SizeOnDisk, Is.EqualTo(100));
            Mocker.GetMock<IEpisodeService>().Verify(s => s.GetEpisodeBySeries(It.IsAny<int>()), Times.Never());
            Mocker.GetMock<IMediaFileService>().Verify(s => s.GetFilesBySeries(It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void should_tolerate_profile_removed_during_statistics_read()
        {
            _profiles.RemoveAll(p => p.Id == 2);

            var statistics = Subject.SeriesStatistics(1, 1);

            Assert.That(statistics.QualityTracks.Select(t => t.TrackId), Is.EqualTo(new[] { 10 }));
            Assert.That(statistics.SizeOnDisk, Is.EqualTo(100));
        }

        [Test]
        public void should_ignore_unmonitored_missing_episodes_and_dangling_file_links()
        {
            _episodes[1].Monitored = false;
            _links.Add(new EpisodeTrackFile { EpisodeId = 2, TrackId = 20, EpisodeFileId = 999 });

            var statistics = Subject.SeriesStatistics(1, 1);
            var additional = statistics.QualityTracks.Single(t => t.TrackId == 20);

            Assert.That(additional.EpisodeCount, Is.EqualTo(1));
            Assert.That(additional.EpisodeFileCount, Is.EqualTo(1));
            Assert.That(additional.MissingCount, Is.Zero);
        }
    }
}
