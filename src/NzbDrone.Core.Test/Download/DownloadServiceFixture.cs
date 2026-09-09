using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class DownloadServiceFixture : CoreTest<DownloadService>
    {
        private RemoteEpisode _parseResult;
        private List<IDownloadClient> _downloadClients;

        [SetUp]
        public void Setup()
        {
            _downloadClients = new List<IDownloadClient>();
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.GetTrackedDownloads()).Returns(new List<TrackedDownload>());

            Mocker.GetMock<IProvideDownloadClient>()
                .Setup(v => v.GetDownloadClients(It.IsAny<DownloadProtocol>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<HashSet<int>>()))
                .Returns<DownloadProtocol, int, bool, HashSet<int>>((v, i, f, t) => _downloadClients.Where(d => d.Protocol == v));

            Mocker.GetMock<IProvideDownloadClient>()
                .Setup(v => v.GetDownloadClient(It.IsAny<DownloadProtocol>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<HashSet<int>>()))
                .Returns<DownloadProtocol, int, bool, HashSet<int>>((v, i, f, t) => _downloadClients.FirstOrDefault(d => d.Protocol == v));

            var episodes = Builder<Episode>.CreateListOfSize(2)
                .TheFirst(1).With(s => s.Id = 12)
                .TheNext(1).With(s => s.Id = 99)
                .All().With(s => s.SeriesId = 5)
                .Build().ToList();

            var releaseInfo = Builder<ReleaseInfo>.CreateNew()
                .With(v => v.DownloadProtocol = DownloadProtocol.Usenet)
                .With(v => v.DownloadUrl = "http://test.site/download1.ext")
                .Build();

            _parseResult = Builder<RemoteEpisode>.CreateNew()
                   .With(c => c.Series = Builder<Series>.CreateNew().Build())
                   .With(c => c.Release = releaseInfo)
                   .With(c => c.Episodes = episodes)
                   .Build();
        }

        private Mock<IDownloadClient> WithUsenetClient()
        {
            var mock = new Mock<IDownloadClient>(MockBehavior.Default);
            mock.SetupGet(s => s.Definition).Returns(Builder<IndexerDefinition>.CreateNew().Build());

            _downloadClients.Add(mock.Object);

            mock.SetupGet(v => v.Protocol).Returns(DownloadProtocol.Usenet);

            return mock;
        }

        private Mock<IDownloadClient> WithTorrentClient()
        {
            var mock = new Mock<IDownloadClient>(MockBehavior.Default);
            mock.SetupGet(s => s.Definition).Returns(Builder<IndexerDefinition>.CreateNew().Build());

            _downloadClients.Add(mock.Object);

            mock.SetupGet(v => v.Protocol).Returns(DownloadProtocol.Torrent);

            return mock;
        }

        [Test]
        public async Task should_capture_selected_profiles_before_client_receives_release()
        {
            var client = WithUsenetClient();
            var primary = new QualityProfile { Id = 1, Cutoff = 1 };
            var secondary = new QualityProfile
            {
                Id = 2,
                Cutoff = 100,
                Items = new List<QualityProfileQualityItem>
                {
                    new()
                    {
                        Id = 100,
                        Allowed = true,
                        Items = new List<QualityProfileQualityItem>
                        {
                            new() { Quality = Quality.HDTV1080p, Allowed = true },
                            new() { Quality = Quality.WEBDL1080p, Allowed = true }
                        }
                    }
                }
            };
            _parseResult.Series.QualityTracks = new List<SeriesQualityTrack>
            {
                new() { Id = 10, QualityProfile = primary },
                new() { Id = 20, QualityProfile = secondary }
            };
            _parseResult.TargetQualityTrackIds = [10, 20];
            var captured = new Dictionary<int, string>();
            client.Setup(c => c.Download(It.IsAny<RemoteEpisode>(), It.IsAny<IIndexer>()))
                .Callback<RemoteEpisode, IIndexer>((remote, _) => captured = new Dictionary<int, string>(remote.TargetQualityTrackSignatures));

            await Subject.DownloadReport(_parseResult, null);
            var original = captured[20];
            secondary.Cutoff = 30;

            captured.Keys.Should().BeEquivalentTo(new[] { 10, 20 });
            captured[10].Should().NotBe(captured[20]);
            _parseResult.TargetQualityTrackSignatures[20].Should().Be(original);
            QualityTrackSnapshot.ProfileSignature(secondary).Should().NotBe(original);
        }

        [Test]
        public async Task should_capture_sole_legacy_profile_without_extra_track_choice()
        {
            WithUsenetClient();
            _parseResult.Series.QualityProfile = new QualityProfile { Id = 1 };
            _parseResult.TargetQualityTrackIds = [10];

            await Subject.DownloadReport(_parseResult, null);

            _parseResult.TargetQualityTrackSignatures.Should().ContainSingle().Which.Key.Should().Be(10);
        }

        [Test]
        public async Task should_reuse_queued_release_and_persist_union_of_targets()
        {
            var client = WithUsenetClient();
            _parseResult.TargetQualityTrackIds = [20];
            var queued = _parseResult.Clone();
            queued.TargetQualityTrackIds = [10];
            var tracked = new TrackedDownload
            {
                RemoteEpisode = queued,
                IsTrackable = true,
                State = TrackedDownloadState.Downloading,
                DownloadClient = 7,
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "download-id",
                    DownloadClientInfo = new DownloadClientItemClientInfo { Id = 7, Name = "Client", Type = "Test" }
                }
            };
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.GetTrackedDownloads()).Returns(new List<TrackedDownload> { tracked });
            EpisodeGrabbedEvent grabbed = null;
            Mocker.GetMock<IEventAggregator>().Setup(e => e.PublishEvent(It.IsAny<EpisodeGrabbedEvent>()))
                .Callback<EpisodeGrabbedEvent>(e => grabbed = e);

            await Subject.DownloadReport(_parseResult, null);

            client.Verify(c => c.Download(It.IsAny<RemoteEpisode>(), It.IsAny<IIndexer>()), Times.Never());
            grabbed.DownloadId.Should().Be("download-id");
            grabbed.NewQualityTrackIds.Should().Equal(20);
            grabbed.Episode.TargetQualityTrackIds.Should().BeEquivalentTo([10, 20]);
            tracked.RemoteEpisode.TargetQualityTrackIds.Should().BeEquivalentTo([10, 20]);
            _parseResult.TargetQualityTrackIds.Should().Equal(20);
        }

        [TestCase(TrackedDownloadState.Failed)]
        [TestCase(TrackedDownloadState.Imported)]
        [TestCase(TrackedDownloadState.Importing)]
        public async Task should_not_attach_targets_to_terminal_or_importing_download(TrackedDownloadState state)
        {
            var client = WithUsenetClient();
            _parseResult.TargetQualityTrackIds = [20];
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.GetTrackedDownloads()).Returns(new List<TrackedDownload>
            {
                new() { RemoteEpisode = _parseResult, IsTrackable = true, State = state }
            });

            await Subject.DownloadReport(_parseResult, null);

            client.Verify(c => c.Download(It.IsAny<RemoteEpisode>(), It.IsAny<IIndexer>()), Times.Once());
        }

        [Test]
        public async Task Download_report_should_publish_on_grab_event()
        {
            var mock = WithUsenetClient();
            mock.Setup(s => s.Download(It.IsAny<RemoteEpisode>(), It.IsAny<IIndexer>()));

            await Subject.DownloadReport(_parseResult, null);

            VerifyEventPublished<EpisodeGrabbedEvent>();
        }

        [Test]
        public async Task Download_report_should_grab_using_client()
        {
            var mock = WithUsenetClient();
            mock.Setup(s => s.Download(It.IsAny<RemoteEpisode>(), It.IsAny<IIndexer>()));

            await Subject.DownloadReport(_parseResult, null);

            mock.Verify(s => s.Download(It.IsAny<RemoteEpisode>(), It.IsAny<IIndexer>()), Times.Once());
        }

        [Test]
        public void Download_report_should_not_publish_on_failed_grab_event()
        {
            var mock = WithUsenetClient();
            mock.Setup(s => s.Download(It.IsAny<RemoteEpisode>(), It.IsAny<IIndexer>()))
                .Throws(new WebException());

            Assert.ThrowsAsync<DownloadClientUnavailableException>(async () => await Subject.DownloadReport(_parseResult, null));

            VerifyEventNotPublished<EpisodeGrabbedEvent>();
        }

        [Test]
        public void Download_report_should_trigger_indexer_backoff_on_indexer_error()
        {
            var mock = WithUsenetClient();
            mock.Setup(s => s.Download(It.IsAny<RemoteEpisode>(), It.IsAny<IIndexer>()))
                .Callback<RemoteEpisode, IIndexer>((v, indexer) =>
                {
                    throw new ReleaseDownloadException(v.Release, "Error", new WebException());
                });

            Assert.ThrowsAsync<ReleaseDownloadException>(async () => await Subject.DownloadReport(_parseResult, null));

            Mocker.GetMock<IIndexerStatusService>()
                .Verify(v => v.RecordFailure(It.IsAny<int>(), It.IsAny<TimeSpan>()), Times.Once());
        }

        [Test]
        public void Download_report_should_trigger_indexer_backoff_on_http429_with_long_time()
        {
            var request = new HttpRequest("http://my.indexer.com");
            var response = new HttpResponse(request, new HttpHeader(), Array.Empty<byte>(), (HttpStatusCode)429);
            response.Headers["Retry-After"] = "300";

            var mock = WithUsenetClient();
            mock.Setup(s => s.Download(It.IsAny<RemoteEpisode>(), It.IsAny<IIndexer>()))
                .Callback<RemoteEpisode, IIndexer>((v, indexer) =>
                {
                    throw new ReleaseDownloadException(v.Release, "Error", new TooManyRequestsException(request, response));
                });

            Assert.ThrowsAsync<ReleaseDownloadException>(async () => await Subject.DownloadReport(_parseResult, null));

            Mocker.GetMock<IIndexerStatusService>()
                .Verify(v => v.RecordFailure(It.IsAny<int>(), TimeSpan.FromMinutes(5.0)), Times.Once());
        }

        [Test]
        public void Download_report_should_trigger_indexer_backoff_on_http429_based_on_date()
        {
            var request = new HttpRequest("http://my.indexer.com");
            var response = new HttpResponse(request, new HttpHeader(), Array.Empty<byte>(), (HttpStatusCode)429);
            response.Headers["Retry-After"] = DateTime.UtcNow.AddSeconds(300).ToString("r");

            var mock = WithUsenetClient();
            mock.Setup(s => s.Download(It.IsAny<RemoteEpisode>(), It.IsAny<IIndexer>()))
                .Callback<RemoteEpisode, IIndexer>((v, indexer) =>
                {
                    throw new ReleaseDownloadException(v.Release, "Error", new TooManyRequestsException(request, response));
                });

            Assert.ThrowsAsync<ReleaseDownloadException>(async () => await Subject.DownloadReport(_parseResult, null));

            Mocker.GetMock<IIndexerStatusService>()
                .Verify(v => v.RecordFailure(It.IsAny<int>(),
                    It.IsInRange<TimeSpan>(TimeSpan.FromMinutes(4.9), TimeSpan.FromMinutes(5.1), Moq.Range.Inclusive)),
                    Times.Once());
        }

        [Test]
        public void Download_report_should_not_trigger_indexer_backoff_on_downloadclient_error()
        {
            var mock = WithUsenetClient();
            mock.Setup(s => s.Download(It.IsAny<RemoteEpisode>(), It.IsAny<IIndexer>()))
                .Throws(new DownloadClientException("Some Error"));

            Assert.ThrowsAsync<DownloadClientUnavailableException>(async () => await Subject.DownloadReport(_parseResult, null));

            Mocker.GetMock<IIndexerStatusService>()
                .Verify(v => v.RecordFailure(It.IsAny<int>(), It.IsAny<TimeSpan>()), Times.Never());
        }

        [Test]
        public void Download_report_should_not_trigger_indexer_backoff_on_indexer_404_error()
        {
            var mock = WithUsenetClient();
            mock.Setup(s => s.Download(It.IsAny<RemoteEpisode>(), It.IsAny<IIndexer>()))
                .Callback<RemoteEpisode, IIndexer>((v, indexer) =>
                {
                    throw new ReleaseUnavailableException(v.Release, "Error", new WebException());
                });

            Assert.ThrowsAsync<ReleaseUnavailableException>(async () => await Subject.DownloadReport(_parseResult, null));

            Mocker.GetMock<IIndexerStatusService>()
                .Verify(v => v.RecordFailure(It.IsAny<int>(), It.IsAny<TimeSpan>()), Times.Never());
        }

        [Test]
        public void should_not_attempt_download_if_client_isnt_configured()
        {
            Assert.ThrowsAsync<DownloadClientUnavailableException>(async () => await Subject.DownloadReport(_parseResult, null));

            Mocker.GetMock<IDownloadClient>().Verify(c => c.Download(It.IsAny<RemoteEpisode>(), It.IsAny<IIndexer>()), Times.Never());
            VerifyEventNotPublished<EpisodeGrabbedEvent>();
        }

        [Test]
        public async Task should_attempt_download_even_if_client_is_disabled()
        {
            var mockUsenet = WithUsenetClient();

            Mocker.GetMock<IDownloadClientStatusService>()
                  .Setup(v => v.GetBlockedProviders())
                  .Returns(new List<DownloadClientStatus>
                  {
                      new DownloadClientStatus
                      {
                          ProviderId = _downloadClients.First().Definition.Id,
                          DisabledTill = DateTime.UtcNow.AddHours(3)
                      }
                  });

            await Subject.DownloadReport(_parseResult, null);

            Mocker.GetMock<IDownloadClientStatusService>().Verify(c => c.GetBlockedProviders(), Times.Never());
            mockUsenet.Verify(c => c.Download(It.IsAny<RemoteEpisode>(), It.IsAny<IIndexer>()), Times.Once());
            VerifyEventPublished<EpisodeGrabbedEvent>();
        }

        [Test]
        public async Task should_send_download_to_correct_usenet_client()
        {
            var mockTorrent = WithTorrentClient();
            var mockUsenet = WithUsenetClient();

            await Subject.DownloadReport(_parseResult, null);

            mockTorrent.Verify(c => c.Download(It.IsAny<RemoteEpisode>(), It.IsAny<IIndexer>()), Times.Never());
            mockUsenet.Verify(c => c.Download(It.IsAny<RemoteEpisode>(), It.IsAny<IIndexer>()), Times.Once());
        }

        [Test]
        public async Task should_send_download_to_correct_torrent_client()
        {
            var mockTorrent = WithTorrentClient();
            var mockUsenet = WithUsenetClient();

            _parseResult.Release.DownloadProtocol = DownloadProtocol.Torrent;

            await Subject.DownloadReport(_parseResult, null);

            mockTorrent.Verify(c => c.Download(It.IsAny<RemoteEpisode>(), It.IsAny<IIndexer>()), Times.Once());
            mockUsenet.Verify(c => c.Download(It.IsAny<RemoteEpisode>(), It.IsAny<IIndexer>()), Times.Never());
        }
    }
}
