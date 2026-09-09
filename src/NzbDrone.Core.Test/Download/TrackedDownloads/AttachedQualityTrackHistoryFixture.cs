using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Download.TrackedDownloads
{
    [TestFixture]
    public class AttachedQualityTrackHistoryFixture : CoreTest<HistoryService>
    {
        [Test]
        public void should_keep_prior_import_when_new_target_joins_same_download()
        {
            var remote = new RemoteEpisode
            {
                Series = new Series { Id = 1 },
                Episodes = [new Episode { Id = 1, SeriesId = 1 }],
                ParsedEpisodeInfo = new ParsedEpisodeInfo(),
                Release = new ReleaseInfo { Title = "The.Office.S03E01", Guid = "same-release" },
                TargetQualityTrackIds = [10, 20],
                TargetQualityTrackSignatures = new Dictionary<int, string> { [10] = "ten", [20] = "twenty" }
            };
            EpisodeHistory attached = null;
            Mocker.GetMock<IHistoryRepository>().Setup(r => r.Insert(It.IsAny<EpisodeHistory>()))
                .Callback<EpisodeHistory>(h => attached = h);

            Subject.Handle(new EpisodeGrabbedEvent(remote) { DownloadId = "same-download", NewQualityTrackIds = [20] });

            QualityTrackSnapshot.ReadTargets(attached.Data).Should().Equal(20);
            QualityTrackSnapshot.ReadSignatures(attached.Data).Should().ContainSingle().Which.Key.Should().Be(20);
            var importedFirst = new EpisodeHistory
            {
                EpisodeId = 1,
                EventType = EpisodeHistoryEventType.DownloadFolderImported,
                Data = new Dictionary<string, string> { ["qualityTrackIds"] = "[10]" }
            };
            var importedSecond = new EpisodeHistory
            {
                EpisodeId = 1,
                EventType = EpisodeHistoryEventType.DownloadFolderImported,
                Data = new Dictionary<string, string> { ["qualityTrackIds"] = "[20]" }
            };
            var tracked = new TrackedDownload { RemoteEpisode = remote, DownloadItem = new DownloadClientItem() };
            var verifier = new TrackedDownloadAlreadyImported(TestLogger);

            verifier.IsImported(tracked, [attached, importedFirst]).Should().BeFalse();
            verifier.IsImported(tracked, [importedSecond, attached, importedFirst]).Should().BeTrue();
        }
    }
}
