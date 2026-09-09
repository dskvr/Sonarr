using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Core.Tv.Events;

namespace NzbDrone.Core.Test.Download.DownloadHistoryTests
{
    [TestFixture]
    public class QualityTrackDownloadHistoryServiceFixture : DbTest<DownloadHistoryService, DownloadHistory>
    {
        private Series _series;
        private RemoteEpisode _remote;
        private DownloadClientItem _item;
        private TrackedDownload _tracked;

        [SetUp]
        public void Setup()
        {
            Mocker.SetConstant<IDownloadHistoryRepository>(Mocker.Resolve<DownloadHistoryRepository>());
            _series = new Series { Id = 1, Path = TempFolder };
            _remote = new RemoteEpisode
            {
                Series = _series,
                Release = new ReleaseInfo { Guid = "release-guid", Title = "The.Office.S03E01", IndexerId = 3, DownloadProtocol = DownloadProtocol.Torrent },
                TargetQualityTrackIds = [10, 20],
                TargetQualityTrackSignatures = new Dictionary<int, string> { [10] = "ten", [20] = "twenty" }
            };
            _item = new DownloadClientItem
            {
                DownloadId = "download-id",
                Title = "The.Office.S03E01",
                DownloadClientInfo = new DownloadClientItemClientInfo { Id = 2, Type = "Torrent", Name = "Client", Protocol = DownloadProtocol.Torrent }
            };
            _tracked = new TrackedDownload { RemoteEpisode = _remote, DownloadItem = _item, DownloadClient = 2, Protocol = DownloadProtocol.Torrent };
        }

        private void Grab()
        {
            Subject.Handle(new EpisodeGrabbedEvent(_remote)
            {
                DownloadId = _item.DownloadId,
                DownloadClientId = 2,
                DownloadClient = "Torrent",
                DownloadClientName = "Client"
            });
        }

        private EpisodeImportedEvent Import(bool newDownload = true, bool includeDownloadId = true)
        {
            var local = new LocalEpisode { Path = "download.mkv", Series = _series, TargetQualityTrackIds = [20] };
            var file = new EpisodeFile { Id = 50, SeriesId = 1, RelativePath = "episode.mkv" };
            var message = new EpisodeImportedEvent(local, file, [], newDownload, includeDownloadId ? _item : null)
            {
                DownloadClientInfo = _item.DownloadClientInfo
            };
            Subject.Handle(message);
            return message;
        }

        [Test]
        public void should_preserve_union_receipt_and_signatures_through_database_reload()
        {
            Grab();
            _remote.TargetQualityTrackIds.Clear();
            _remote.TargetQualityTrackSignatures.Clear();
            var restored = new DownloadHistoryService(Mocker.Resolve<DownloadHistoryRepository>(), Mocker.GetMock<IHistoryService>().Object).GetLatestGrab(_item.DownloadId);

            QualityTrackSnapshot.ReadTargets(restored.Data).Should().Equal(10, 20);
            QualityTrackSnapshot.ReadSignatures(restored.Data).Should().BeEquivalentTo(new Dictionary<int, string> { [10] = "ten", [20] = "twenty" });
            restored.Release.Guid.Should().Be("release-guid");
            restored.DownloadClientId.Should().Be(2);
        }

        [Test]
        public void should_require_complete_receipt_after_partial_file_import_and_reset_on_regrab()
        {
            Grab();
            Import();

            Subject.DownloadAlreadyImported(_item.DownloadId).Should().BeFalse();
            Subject.GetLatestDownloadHistoryItem(_item.DownloadId).EventType.Should().Be(DownloadHistoryEventType.DownloadGrabbed);
            Subject.Handle(new DownloadCompletedEvent(_tracked, 1, [], null));
            Subject.DownloadAlreadyImported(_item.DownloadId).Should().BeTrue();
            Subject.GetLatestDownloadHistoryItem(_item.DownloadId).EventType.Should().Be(DownloadHistoryEventType.DownloadImported);

            Grab();

            Subject.DownloadAlreadyImported(_item.DownloadId).Should().BeFalse();
            Subject.GetLatestGrab(_item.DownloadId).Should().NotBeNull();
        }

        [TestCase(true)]
        [TestCase(false)]
        public void should_record_failed_and_ignored_terminal_receipts(bool ignored)
        {
            Grab();

            if (ignored)
            {
                Subject.Handle(new DownloadIgnoredEvent { SeriesId = 1, DownloadId = _item.DownloadId, SourceTitle = _item.Title, DownloadClientInfo = _item.DownloadClientInfo });
            }
            else
            {
                Subject.Handle(new DownloadFailedEvent { SeriesId = 1, DownloadId = _item.DownloadId, SourceTitle = _item.Title, TrackedDownload = _tracked });
            }

            Subject.GetLatestDownloadHistoryItem(_item.DownloadId).EventType.Should().Be(ignored ? DownloadHistoryEventType.DownloadIgnored : DownloadHistoryEventType.DownloadFailed);
            Subject.DownloadAlreadyImported(_item.DownloadId).Should().BeFalse();
            QualityTrackSnapshot.ReadTargets(Subject.GetLatestGrab(_item.DownloadId).Data).Should().Equal(10, 20);
        }

        [Test]
        public void should_preserve_legacy_grab_without_target_metadata()
        {
            _remote.TargetQualityTrackIds = null;
            _remote.TargetQualityTrackSignatures = null;

            Grab();

            QualityTrackSnapshot.ReadTargets(StoredModel.Data).Should().BeNull();
            QualityTrackSnapshot.ReadSignatures(StoredModel.Data).Should().BeNull();
        }

        [TestCase(null)]
        [TestCase("")]
        public void should_not_store_grab_without_download_id(string downloadId)
        {
            _item.DownloadId = downloadId;

            Grab();

            AllStoredModels.Should().BeEmpty();
        }

        [Test]
        public void should_not_mark_rescan_or_untracked_failure_as_download_history()
        {
            Import(false);
            Subject.Handle(new DownloadFailedEvent { TrackedDownload = null });

            AllStoredModels.Should().BeEmpty();
        }

        [TestCase(true)]
        [TestCase(false)]
        public void should_resolve_missing_manual_import_download_id_from_episode_history(bool found)
        {
            Mocker.GetMock<IHistoryService>().Setup(s => s.FindDownloadId(It.IsAny<EpisodeImportedEvent>())).Returns(found ? _item.DownloadId : null);

            Import(includeDownloadId: false);

            if (found)
            {
                StoredModel.EventType.Should().Be(DownloadHistoryEventType.FileImported);
                StoredModel.DownloadId.Should().Be(_item.DownloadId);
                Subject.GetLatestDownloadHistoryItem(_item.DownloadId).Should().BeNull();
                Subject.GetLatestGrab(_item.DownloadId).Should().BeNull();
                Subject.DownloadAlreadyImported(_item.DownloadId).Should().BeFalse();
            }
            else
            {
                AllStoredModels.Should().BeEmpty();
            }
        }

        [Test]
        public void should_remove_only_deleted_series_receipts()
        {
            Grab();
            _series.Id = 2;
            _item.DownloadId = "another-download";
            Grab();

            Subject.Handle(new SeriesDeletedEvent([new Series { Id = 1 }], false, false));

            AllStoredModels.Select(h => h.SeriesId).Should().Equal(2);
            Subject.GetLatestGrab("download-id").Should().BeNull();
        }
    }
}
