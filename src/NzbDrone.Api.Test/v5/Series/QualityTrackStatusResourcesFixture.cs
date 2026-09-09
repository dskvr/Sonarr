using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.SeriesStats;
using Sonarr.Api.V5.Queue;
using Sonarr.Api.V5.Series;

namespace NzbDrone.Api.Test.v5.Series
{
    [TestFixture]
    public class QualityTrackStatusResourcesFixture
    {
        [Test]
        public void track_statistics_should_preserve_scalar_presence_and_report_additional_progress_separately()
        {
            var tracks = new List<QualityTrackStatistics>
            {
                new QualityTrackStatistics { TrackId = 10, QualityProfileId = 1, EpisodeCount = 10, EpisodeFileCount = 10 },
                new QualityTrackStatistics { TrackId = 20, QualityProfileId = 2, EpisodeCount = 10, EpisodeFileCount = 6, CutoffUnmetCount = 2 }
            };
            var series = new SeriesStatistics { EpisodeCount = 10, EpisodeFileCount = 10, SizeOnDisk = 100, QualityTracks = tracks };
            var season = new SeasonStatistics { EpisodeCount = 10, EpisodeFileCount = 10, SizeOnDisk = 100, QualityTracks = tracks };

            var seriesResource = series.ToResource(new List<SeasonResource>());
            var seasonResource = season.ToResource();

            seriesResource.PercentOfEpisodes.Should().Be(100);
            seasonResource.PercentOfEpisodes.Should().Be(100);
            seriesResource.SizeOnDisk.Should().Be(100);
            seriesResource.QualityTracks.Should().BeEquivalentTo(seasonResource.QualityTracks);
            seriesResource.QualityTracks.Should().ContainSingle(t => t.TrackId == 20 && t.QualityProfileId == 2 && t.EpisodeCount == 10 && t.EpisodeFileCount == 6 && t.MissingCount == 4 && t.CutoffUnmetCount == 2);
        }

        [Test]
        public void single_profile_statistics_should_not_add_version_progress()
        {
            new SeriesStatistics().ToResource(new List<SeasonResource>()).QualityTracks.Should().BeEmpty();
            new SeasonStatistics().ToResource().QualityTracks.Should().BeEmpty();
        }

        [Test]
        public void queue_should_expose_persisted_target_ids_without_reclassifying_them()
        {
            var queue = new NzbDrone.Core.Queue.Queue
            {
                RemoteEpisode = new RemoteEpisode { TargetQualityTrackIds = new List<int> { 20, 30 } }
            };

            queue.ToResource(false, false).TargetQualityTrackIds.Should().Equal(20, 30);
            new NzbDrone.Core.Queue.Queue().ToResource(false, false).TargetQualityTrackIds.Should().BeEmpty();
        }
    }
}
