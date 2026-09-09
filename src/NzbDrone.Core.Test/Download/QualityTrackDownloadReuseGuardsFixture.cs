using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class QualityTrackDownloadReuseGuardsFixture : CoreTest<DownloadService>
    {
        private RemoteEpisode _requested;
        private TrackedDownload _queued;
        private Mock<IDownloadClient> _client;

        [SetUp]
        public void Setup()
        {
            var profile = new QualityProfile { Id = 1 };
            var second = new QualityProfile { Id = 2 };
            var series = new Series
            {
                Id = 1,
                QualityProfile = profile,
                QualityTracks = new List<SeriesQualityTrack>
                {
                    new() { Id = 10, QualityProfileId = 1, QualityProfile = profile, IsPrimary = true, Enabled = true },
                    new() { Id = 20, QualityProfileId = 2, QualityProfile = second, Enabled = true }
                }
            };
            _requested = new RemoteEpisode
            {
                Series = series,
                Episodes = [new Episode { Id = 1, SeriesId = 1 }],
                Release = new ReleaseInfo { Guid = "same-guid", Title = "Series.S01E01.1080p.HDTV", IndexerId = 1, DownloadProtocol = DownloadProtocol.Usenet },
                TargetQualityTrackIds = [20]
            };
            var remote = _requested.Clone();
            remote.Release = _requested.Release.Clone();
            remote.TargetQualityTrackIds = [10];
            remote.TargetQualityTrackSignatures = new Dictionary<int, string> { [10] = "original-target-signature" };
            _queued = new TrackedDownload
            {
                RemoteEpisode = remote,
                State = TrackedDownloadState.Downloading,
                IsTrackable = true,
                DownloadClient = 7,
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "existing-download",
                    DownloadClientInfo = new DownloadClientItemClientInfo { Id = 7, Name = "Client", Type = "Local" }
                }
            };
            _client = new Mock<IDownloadClient>();
            _client.SetupGet(c => c.Definition).Returns(new DownloadClientDefinition { Id = 7, Name = "Client" });
            _client.Setup(c => c.Download(It.IsAny<RemoteEpisode>(), It.IsAny<IIndexer>())).ReturnsAsync("fresh-download");
            Mocker.GetMock<IProvideDownloadClient>().Setup(p => p.Get(7)).Returns(_client.Object);
            Mocker.GetMock<IProvideDownloadClient>()
                .Setup(p => p.GetDownloadClients(It.IsAny<DownloadProtocol>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<HashSet<int>>()))
                .Returns(new[] { _client.Object });
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.GetTrackedDownloads()).Returns(new List<TrackedDownload> { _queued });
        }

        [TestCase("client")]
        [TestCase("series")]
        [TestCase("indexer")]
        [TestCase("guid")]
        [TestCase("episodes")]
        [TestCase("untracked")]
        [TestCase("unknown-episode")]
        [TestCase("unknown-series")]
        [TestCase("unknown-release")]
        public async Task should_not_attach_targets_to_unrelated_or_untrackable_download(string difference)
        {
            var original = _queued.RemoteEpisode;
            switch (difference)
            {
                case "client":
                    _queued.DownloadClient = 8;
                    break;
                case "series":
                    _queued.RemoteEpisode.Series = new Series { Id = 2 };
                    break;
                case "indexer":
                    _queued.RemoteEpisode.Release.IndexerId = 2;
                    break;
                case "guid":
                    _queued.RemoteEpisode.Release.Guid = "another-guid";
                    break;
                case "episodes":
                    _queued.RemoteEpisode.Episodes = [new Episode { Id = 2, SeriesId = 1 }];
                    break;
                case "untracked":
                    _queued.IsTrackable = false;
                    break;
                case "unknown-episode":
                    _queued.RemoteEpisode = null;
                    break;
                case "unknown-series":
                    _queued.RemoteEpisode.Series = null;
                    break;
                case "unknown-release":
                    _queued.RemoteEpisode.Release = null;
                    break;
            }

            await Subject.DownloadReport(_requested, difference == "client" ? 7 : null);

            _client.Verify(c => c.Download(_requested, It.IsAny<IIndexer>()), Times.Once());
            original.TargetQualityTrackIds.Should().Equal(10);
            original.TargetQualityTrackSignatures[10].Should().Be("original-target-signature");
        }

        [TestCase(TrackedDownloadState.ImportBlocked)]
        [TestCase(TrackedDownloadState.ImportPending)]
        public async Task should_reuse_matching_client_and_preserve_original_target_signature(TrackedDownloadState state)
        {
            _queued.State = state;
            EpisodeGrabbedEvent attached = null;
            Mocker.GetMock<IEventAggregator>().Setup(e => e.PublishEvent(It.IsAny<EpisodeGrabbedEvent>())).Callback<EpisodeGrabbedEvent>(e => attached = e);

            await Subject.DownloadReport(_requested, 7);

            _client.Verify(c => c.Download(It.IsAny<RemoteEpisode>(), It.IsAny<IIndexer>()), Times.Never());
            attached.DownloadId.Should().Be("existing-download");
            attached.NewQualityTrackIds.Should().Equal(20);
            attached.Episode.TargetQualityTrackIds.Should().BeEquivalentTo(new[] { 10, 20 });
            attached.Episode.TargetQualityTrackSignatures[10].Should().Be("original-target-signature");
            attached.Episode.TargetQualityTrackSignatures.Keys.Should().BeEquivalentTo(new[] { 10, 20 });
        }

        [Test]
        public async Task should_treat_repeated_target_selection_as_idempotent()
        {
            _queued.RemoteEpisode.TargetQualityTrackIds = [20];
            _queued.RemoteEpisode.TargetQualityTrackSignatures = new Dictionary<int, string> { [20] = "original-target-signature" };

            await Subject.DownloadReport(_requested, null);

            _client.Verify(c => c.Download(It.IsAny<RemoteEpisode>(), It.IsAny<IIndexer>()), Times.Never());
            VerifyEventNotPublished<EpisodeGrabbedEvent>();
            _queued.RemoteEpisode.TargetQualityTrackSignatures.Single().Value.Should().Be("original-target-signature");
        }
    }
}
