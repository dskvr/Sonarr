using System;
using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Tv;
using NzbDrone.SignalR;
using Sonarr.Api.V5.Episodes;

namespace NzbDrone.Api.Test.v5.Episodes
{
    [TestFixture]
    public class EpisodeQualityTrackResourceFixture
    {
        private NzbDrone.Core.Tv.Series _series;
        private Episode _episode;
        private EpisodeFile _file;
        private Mock<IUpgradableSpecification> _upgradable;
        private Mock<ICustomFormatCalculationService> _formats;

        [SetUp]
        public void Setup()
        {
            var primaryProfile = new QualityProfile { Id = 1 };
            _series = new NzbDrone.Core.Tv.Series
            {
                Id = 1,
                Path = "/series",
                QualityProfileId = 1,
                QualityProfile = primaryProfile,
                QualityTracks = new List<SeriesQualityTrack>
                {
                    new SeriesQualityTrack { Id = 10, QualityProfileId = 1, IsPrimary = true, Enabled = true, QualityProfile = primaryProfile },
                    new SeriesQualityTrack { Id = 20, QualityProfileId = 2, Enabled = true, QualityProfile = new QualityProfile { Id = 2 } },
                    new SeriesQualityTrack { Id = 30, QualityProfileId = 3, Enabled = false, QualityProfile = new QualityProfile { Id = 3 } }
                }
            };
            _file = new EpisodeFile { Id = 100, SeriesId = 1, RelativePath = "episode.mkv", Quality = new QualityModel(Quality.HDTV720p) };
            var links = new List<EpisodeTrackFile>
            {
                new EpisodeTrackFile { EpisodeId = 1, TrackId = 10, EpisodeFileId = 100, EpisodeFile = _file },
                new EpisodeTrackFile { EpisodeId = 1, TrackId = 30, EpisodeFileId = 100, EpisodeFile = _file },
                new EpisodeTrackFile { EpisodeId = 2, TrackId = 30, EpisodeFileId = 100, EpisodeFile = _file }
            };
            _file.TrackFiles = links;
            _episode = new Episode { Id = 1, SeriesId = 1, EpisodeFileId = 100, EpisodeFile = _file, TrackFiles = links.GetRange(0, 2) };
            _upgradable = new Mock<IUpgradableSpecification>();
            _formats = new Mock<ICustomFormatCalculationService>();
            _formats.Setup(f => f.ParseCustomFormat(It.IsAny<EpisodeFile>(), It.IsAny<NzbDrone.Core.Tv.Series>())).Returns(new List<CustomFormat>());
        }

        [Test]
        public void missing_additional_version_should_not_use_legacy_file_fallback()
        {
            var resource = _episode.ToResource();

            resource.MapQualityTracks(_episode, _series, true, _upgradable.Object, _formats.Object);

            resource.EpisodeFileId.Should().Be(100);
            resource.HasFile.Should().BeTrue();
            resource.QualityTracks.Should().ContainSingle(t => t.TrackId == 20 && !t.HasFile && t.EpisodeFileId == 0);
            resource.QualityTracks.Should().ContainSingle(t => t.TrackId == 30 && !t.Enabled && t.HasFile);
        }

        [Test]
        public void shared_file_should_appear_once_with_all_episode_and_track_owners()
        {
            var resource = _episode.ToResource();

            resource.MapQualityTracks(_episode, _series, true, _upgradable.Object, _formats.Object);

            resource.EpisodeFiles.Should().ContainSingle();
            resource.EpisodeFiles[0].QualityTrackIds.Should().Equal(10, 30);
            resource.EpisodeFiles[0].EpisodeIds.Should().Equal(1, 2);
        }

        [Test]
        public void excluded_file_subresource_should_still_report_status_without_file_details()
        {
            var resource = _episode.ToResource();

            resource.MapQualityTracks(_episode, _series, false, _upgradable.Object, _formats.Object);

            resource.EpisodeFiles.Should().BeNull();
            resource.QualityTracks.Should().HaveCount(3);
        }

        [Test]
        public void cutoff_should_be_evaluated_with_each_tracks_own_profile()
        {
            _upgradable.Setup(s => s.QualityCutoffNotMet(It.Is<QualityProfile>(p => p.Id == 3), _file.Quality, null)).Returns(true);
            var resource = _episode.ToResource();

            resource.MapQualityTracks(_episode, _series, false, _upgradable.Object, _formats.Object);

            resource.QualityTracks.Should().ContainSingle(t => t.TrackId == 10 && !t.QualityCutoffNotMet);
            resource.QualityTracks.Should().ContainSingle(t => t.TrackId == 30 && t.QualityCutoffNotMet);
            _upgradable.Verify(s => s.QualityCutoffNotMet(It.Is<QualityProfile>(p => p.Id == 2), It.IsAny<QualityModel>(), null), Times.Never());
        }

        [Test]
        public void custom_format_score_should_be_specific_to_each_shared_file_owner()
        {
            var format = new CustomFormat { Id = 1 };
            _series.QualityTracks.Value[0].QualityProfile.Value.FormatItems.Add(new ProfileFormatItem { Format = format, Score = 100 });
            _series.QualityTracks.Value[2].QualityProfile.Value.FormatItems.Add(new ProfileFormatItem { Format = format, Score = -50 });
            _formats.Setup(f => f.ParseCustomFormat(_file, _series)).Returns(new List<CustomFormat> { format });
            var resource = _episode.ToResource();

            resource.MapQualityTracks(_episode, _series, false, _upgradable.Object, _formats.Object);

            resource.QualityTracks.Should().ContainSingle(t => t.TrackId == 10 && t.CustomFormatScore == 100);
            resource.QualityTracks.Should().ContainSingle(t => t.TrackId == 30 && t.CustomFormatScore == -50);
        }

        [Test]
        public void episode_list_should_return_track_status_without_requesting_file_subresources()
        {
            var episodeService = new Mock<IEpisodeService>();
            episodeService.Setup(s => s.GetEpisodeBySeries(1)).Returns(new List<Episode> { _episode });
            var seriesService = new Mock<ISeriesService>();
            seriesService.Setup(s => s.GetSeries(1)).Returns(_series);
            var controller = new EpisodeController(seriesService.Object, episodeService.Object, _upgradable.Object, _formats.Object, Mock.Of<IBroadcastSignalRMessage>());

            var result = controller.GetEpisodes(1, null, new List<int>(), null, Array.Empty<EpisodeSubresource>());
            var resource = ((Microsoft.AspNetCore.Http.HttpResults.Ok<List<EpisodeResource>>)result.Result).Value[0];

            resource.QualityTracks.Should().HaveCount(3);
            resource.EpisodeFiles.Should().BeNull();
        }

        [Test]
        public void single_episode_update_should_keep_track_status_in_response()
        {
            var episodeService = new Mock<IEpisodeService>();
            episodeService.Setup(s => s.GetEpisode(1)).Returns(_episode);
            var seriesService = new Mock<ISeriesService>();
            seriesService.Setup(s => s.GetSeries(1)).Returns(_series);
            var controller = new EpisodeController(seriesService.Object, episodeService.Object, _upgradable.Object, _formats.Object, Mock.Of<IBroadcastSignalRMessage>());

            var resource = controller.SetEpisodeMonitored(1, new EpisodeResource { Monitored = true }).Value;

            resource.QualityTracks.Should().HaveCount(3);
            resource.EpisodeFiles.Should().BeNull();
            episodeService.Verify(s => s.SetEpisodeMonitored(1, true), Times.Once());
        }
    }
}
