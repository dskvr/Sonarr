using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Core.Tv.Events;

namespace NzbDrone.Core.Test.Download.TrackedDownloads
{
    [TestFixture]
    public class QualityTrackDownloadFixture : CoreTest<TrackedDownloadService>
    {
        private Series _series;
        private DownloadHistory _grab;
        private DownloadClientDefinition _client;
        private DownloadClientItem _item;

        [SetUp]
        public void Setup()
        {
            _series = new Series
            {
                Id = 1,
                QualityTracks = new List<SeriesQualityTrack>
                {
                    new() { Id = 10, IsPrimary = true, Enabled = true },
                    new() { Id = 20, Enabled = true }
                }
            };
            _grab = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.DownloadGrabbed,
                SeriesId = 1,
                Release = new ReleaseInfo { Guid = "saved-release-guid", IndexerId = 7 },
                Data = new Dictionary<string, string>
                {
                    ["qualityTrackIds"] = "[10,20]",
                    ["qualityTrackSignatures"] = "{\"10\":\"original-ten\",\"20\":\"original-twenty\"}"
                }
            };
            _client = new DownloadClientDefinition { Id = 1, Protocol = DownloadProtocol.Torrent };
            _item = new DownloadClientItem
            {
                Title = "The.Office.S03E01.1080p.HDTV-GROUP",
                DownloadId = "saved-download",
                DownloadClientInfo = new DownloadClientItemClientInfo { Id = 1, Name = "Test" }
            };
            Mocker.GetMock<IDownloadHistoryService>().Setup(s => s.GetLatestGrab(_item.DownloadId)).Returns(() => _grab);
            Mocker.GetMock<IDownloadHistoryService>().Setup(s => s.GetLatestDownloadHistoryItem(_item.DownloadId)).Returns(() => _grab);
            Mocker.GetMock<IHistoryService>().Setup(s => s.FindByDownloadId(_item.DownloadId)).Returns(new List<EpisodeHistory>());
            Mocker.GetMock<IParsingService>()
                .Setup(s => s.Map(It.IsAny<ParsedEpisodeInfo>(), 0, 0, null, null))
                .Returns(() => new RemoteEpisode { Series = _series, Episodes = [new Episode { Id = 1 }] });
        }

        [Test]
        public void should_restore_targets_and_release_identity_after_cache_loss()
        {
            var initial = Subject.TrackDownload(_client, _item);
            initial.RemoteEpisode.TargetQualityTrackIds.Should().Equal(10, 20);
            Subject.StopTracking(_item.DownloadId);

            var restored = Subject.TrackDownload(_client, _item);

            restored.Should().NotBeSameAs(initial);
            restored.RemoteEpisode.TargetQualityTrackIds.Should().Equal(10, 20);
            restored.RemoteEpisode.TargetQualityTrackSignatures.Should().BeEquivalentTo(new Dictionary<int, string> { [10] = "original-ten", [20] = "original-twenty" });
            restored.RemoteEpisode.Release.Guid.Should().Be("saved-release-guid");
            restored.RemoteEpisode.Release.IndexerId.Should().Be(7);
        }

        [Test]
        public void should_preserve_disabled_target_when_refreshing_cached_series()
        {
            var tracked = Subject.TrackDownload(_client, _item);
            _series.QualityTracks.Value[1].Enabled = false;

            Subject.Handle(new SeriesEditedEvent(_series, _series));

            tracked.RemoteEpisode.TargetQualityTrackIds.Should().Equal(10, 20);
            tracked.RemoteEpisode.TargetQualityTrackSignatures[20].Should().Be("original-twenty");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_block_known_grab_that_maps_to_a_different_series(bool episodeHistoryOnly)
        {
            if (episodeHistoryOnly)
            {
                _grab = null;
                Mocker.GetMock<IHistoryService>().Setup(s => s.FindByDownloadId(_item.DownloadId)).Returns(new List<EpisodeHistory>
                {
                    new() { SeriesId = 2, EpisodeId = 99, EventType = EpisodeHistoryEventType.Grabbed, SourceTitle = "Original.Series.S01E01", DownloadId = _item.DownloadId }
                });
            }
            else
            {
                _grab.SeriesId = 2;
                _grab.SourceTitle = "Original.Series.S01E01";
                _grab.Data.Clear();
            }

            var tracked = Subject.TrackDownload(_client, _item);

            tracked.RemoteEpisode.TargetQualityTrackIds.Should().BeEmpty();
            tracked.RemoteEpisode.LegacyQualityTrackTarget.Should().BeFalse();
            tracked.Status.Should().Be(TrackedDownloadStatus.Warning);
            tracked.StatusMessages.Should().ContainSingle().Which.Messages.Should().ContainSingle().Which
                .Should().Contain("series 2").And.Contain("Original.Series.S01E01").And.Contain("parsed series 1");
        }

        [Test]
        public void should_not_invent_primary_intent_for_receipt_without_known_series()
        {
            _grab.SeriesId = 0;
            _grab.Data.Clear();

            var tracked = Subject.TrackDownload(_client, _item);

            tracked.RemoteEpisode.TargetQualityTrackIds.Should().BeNull();
            tracked.RemoteEpisode.LegacyQualityTrackTarget.Should().BeFalse();
        }

        [Test]
        public void should_not_invent_primary_intent_for_uncorrelated_queue_item()
        {
            _grab = null;

            var tracked = Subject.TrackDownload(_client, _item);

            tracked.RemoteEpisode.TargetQualityTrackIds.Should().BeNull();
            tracked.RemoteEpisode.LegacyQualityTrackTarget.Should().BeFalse();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_restore_target_intent_from_correlated_episode_grab_when_download_receipt_is_missing(bool legacy)
        {
            _grab = null;
            var grabbed = new EpisodeHistory
            {
                SeriesId = 1,
                EpisodeId = 1,
                DownloadId = _item.DownloadId,
                EventType = EpisodeHistoryEventType.Grabbed,
                SourceTitle = _item.Title
            };
            if (!legacy)
            {
                grabbed.Data["qualityTrackIds"] = "[20]";
                grabbed.Data["qualityTrackSignatures"] = "{\"20\":\"original-twenty\"}";
            }

            Mocker.GetMock<IHistoryService>().Setup(s => s.FindByDownloadId(_item.DownloadId)).Returns(new List<EpisodeHistory> { grabbed });

            var tracked = Subject.TrackDownload(_client, _item);

            tracked.RemoteEpisode.TargetQualityTrackIds.Should().Equal(legacy ? 10 : 20);
            tracked.RemoteEpisode.LegacyQualityTrackTarget.Should().Be(legacy);
            if (!legacy)
            {
                tracked.RemoteEpisode.TargetQualityTrackSignatures[20].Should().Be("original-twenty");
            }
        }

        [Test]
        public void should_bind_legacy_grab_to_original_primary_only()
        {
            _grab.Data.Clear();

            var tracked = Subject.TrackDownload(_client, _item);

            tracked.RemoteEpisode.TargetQualityTrackIds.Should().Equal(10);
            tracked.RemoteEpisode.LegacyQualityTrackTarget.Should().BeTrue();
        }

        [TestCase("not-json")]
        [TestCase(null)]
        [TestCase("[]")]
        [TestCase("[0]")]
        public void should_keep_invalid_saved_intent_for_manual_handling(string targets)
        {
            _grab.Data["qualityTrackIds"] = targets;

            var tracked = Subject.TrackDownload(_client, _item);

            tracked.RemoteEpisode.TargetQualityTrackIds.Should().BeEmpty();
            tracked.RemoteEpisode.LegacyQualityTrackTarget.Should().BeFalse();
            tracked.Status.Should().Be(TrackedDownloadStatus.Warning);
            tracked.StatusMessages.Should().ContainSingle();
        }
    }
}
