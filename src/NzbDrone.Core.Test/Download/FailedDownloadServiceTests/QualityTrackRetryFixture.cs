using System.Collections.Generic;
using System.Linq;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Download.FailedDownloadServiceTests
{
    [TestFixture]
    public class QualityTrackRetryFixture : CoreTest<RedownloadFailedDownloadService>
    {
        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<IConfigService>().SetupGet(c => c.AutoRedownloadFailed).Returns(true);
            Mocker.GetMock<ISeriesQualityTrackService>().Setup(s => s.GetTracks(1)).Returns(new List<SeriesQualityTrack>
            {
                new() { Id = 10, IsPrimary = true, Enabled = true },
                new() { Id = 20, Enabled = true }
            });
        }

        [TestCase("user")]
        [TestCase("disabled")]
        [TestCase("interactive")]
        public void should_respect_retry_policy_before_resolving_quality_targets(string reason)
        {
            Mocker.GetMock<IConfigService>().SetupGet(c => c.AutoRedownloadFailed).Returns(reason != "disabled");
            var failed = new DownloadFailedEvent
            {
                SeriesId = 1,
                EpisodeIds = [1],
                SkipRedownload = reason == "user",
                ReleaseSource = reason == "interactive" ? ReleaseSourceType.InteractiveSearch : ReleaseSourceType.Rss
            };

            Subject.Handle(failed);

            Mocker.GetMock<IManageCommandQueue>().Verify(q => q.Push(It.IsAny<EpisodeSearchCommand>(), CommandPriority.Normal, CommandTrigger.Unspecified), Times.Never());
            Mocker.GetMock<ISeriesQualityTrackService>().Verify(s => s.GetTracks(It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void should_use_tracked_targets_when_history_describes_an_earlier_grab()
        {
            Mocker.GetMock<IConfigService>().SetupGet(c => c.AutoRedownloadFailedFromInteractiveSearch).Returns(true);
            Subject.Handle(new DownloadFailedEvent
            {
                SeriesId = 1,
                EpisodeIds = [1],
                ReleaseSource = ReleaseSourceType.InteractiveSearch,
                Data = new Dictionary<string, string> { ["qualityTrackIds"] = "[10]" },
                TrackedDownload = new TrackedDownload { RemoteEpisode = new RemoteEpisode { TargetQualityTrackIds = [20] } }
            });

            Mocker.GetMock<IManageCommandQueue>().Verify(q => q.Push(It.Is<EpisodeSearchCommand>(c => c.TargetQualityTrackIds.SequenceEqual(new[] { 20 })), CommandPriority.Normal, CommandTrigger.Unspecified), Times.Once());
        }

        [Test]
        public void should_preserve_legacy_retry_when_no_track_rows_exist()
        {
            Mocker.GetMock<ISeriesQualityTrackService>().Setup(s => s.GetTracks(1)).Returns(new List<SeriesQualityTrack>());

            Subject.Handle(new DownloadFailedEvent { SeriesId = 1, EpisodeIds = [1], TrackedDownload = new TrackedDownload() });

            Mocker.GetMock<IManageCommandQueue>().Verify(q => q.Push(It.Is<EpisodeSearchCommand>(c => c.TargetQualityTrackIds == null), CommandPriority.Normal, CommandTrigger.Unspecified), Times.Once());
        }

        [Test]
        public void should_retry_partial_season_as_episodes_with_original_targets()
        {
            Mocker.GetMock<IEpisodeService>().Setup(s => s.GetEpisode(1)).Returns(new Episode { SeasonNumber = 2 });
            Mocker.GetMock<IEpisodeService>().Setup(s => s.GetEpisodesBySeason(1, 2)).Returns(new List<Episode> { new() { Id = 1 }, new() { Id = 2 }, new() { Id = 3 } });

            Subject.Handle(new DownloadFailedEvent { SeriesId = 1, EpisodeIds = [1, 2], Data = new Dictionary<string, string> { ["qualityTrackIds"] = "[20]" } });

            Mocker.GetMock<IManageCommandQueue>().Verify(q => q.Push(It.Is<EpisodeSearchCommand>(c => c.EpisodeIds.SequenceEqual(new[] { 1, 2 }) && c.TargetQualityTrackIds.SequenceEqual(new[] { 20 })), CommandPriority.Normal, CommandTrigger.Unspecified), Times.Once());
        }

        [TestCase("[20]", 20)]
        [TestCase(null, 10)]
        public void should_retry_only_saved_target_or_legacy_primary(string targets, int expected)
        {
            var failed = new DownloadFailedEvent { SeriesId = 1, EpisodeIds = [1] };

            if (targets != null)
            {
                failed.Data["qualityTrackIds"] = targets;
            }

            Subject.Handle(failed);

            Mocker.GetMock<IManageCommandQueue>().Verify(
                q => q.Push(
                    It.Is<EpisodeSearchCommand>(c => c.TargetQualityTrackIds.SequenceEqual(new[] { expected })),
                    CommandPriority.Normal,
                    CommandTrigger.Unspecified),
                Times.Once());
        }

        [TestCase("invalid")]
        [TestCase("[]")]
        public void should_not_retry_with_invalid_saved_intent(string targets)
        {
            var failed = new DownloadFailedEvent
            {
                SeriesId = 1,
                EpisodeIds = [1],
                Data = new Dictionary<string, string> { ["qualityTrackIds"] = targets }
            };

            Subject.Handle(failed);

            Mocker.GetMock<IManageCommandQueue>().Verify(q => q.Push(It.IsAny<EpisodeSearchCommand>(), CommandPriority.Normal, CommandTrigger.Unspecified), Times.Never());
        }

        [Test]
        public void should_preserve_targets_when_retrying_entire_season()
        {
            Mocker.GetMock<IEpisodeService>().Setup(s => s.GetEpisode(1)).Returns(new Episode { SeasonNumber = 2 });
            Mocker.GetMock<IEpisodeService>().Setup(s => s.GetEpisodesBySeason(1, 2)).Returns(new List<Episode> { new() { Id = 1 }, new() { Id = 2 } });

            Subject.Handle(new DownloadFailedEvent
            {
                SeriesId = 1,
                EpisodeIds = [1, 2],
                Data = new Dictionary<string, string> { ["qualityTrackIds"] = "[20]" }
            });

            Mocker.GetMock<IManageCommandQueue>().Verify(
                q => q.Push(
                    It.Is<SeasonSearchCommand>(c => c.TargetQualityTrackIds.SequenceEqual(new[] { 20 }) && c.SeasonNumber == 2),
                    CommandPriority.Normal,
                    CommandTrigger.Unspecified),
                Times.Once());
        }
    }
}
