using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Download.DownloadApprovedReportsTests
{
    [TestFixture]
    public class QualityTrackSingleDecisionFixture : CoreTest<ProcessDownloadDecisions>
    {
        private RemoteEpisode _remote;

        [SetUp]
        public void Setup()
        {
            _remote = new RemoteEpisode
            {
                Series = new Series { Id = 1 },
                Episodes = [new Episode { Id = 1 }],
                Release = new ReleaseInfo { Title = "Release", Indexer = "Indexer" },
                TargetQualityTrackIds = [20]
            };
        }

        [Test]
        public async Task should_skip_missing_selection_and_reject_permanent_decision()
        {
            (await Subject.ProcessDecision(null, null)).Should().Be(ProcessedDecisionResult.Skipped);
            (await Subject.ProcessDecision(new DownloadDecision(_remote, new DownloadRejection(DownloadRejectionReason.QualityNotWanted, "Not wanted")), null))
                .Should().Be(ProcessedDecisionResult.Rejected);
            Mocker.GetMock<IDownloadService>().Verify(s => s.DownloadReport(It.IsAny<RemoteEpisode>(), It.IsAny<int?>()), Times.Never());
        }

        [Test]
        public async Task should_preserve_single_target_for_delayed_interactive_selection()
        {
            var decision = new DownloadDecision(_remote, new DownloadRejection(DownloadRejectionReason.MinimumAgeDelay, "Delayed", RejectionType.Temporary));

            (await Subject.ProcessDecision(decision, 7)).Should().Be(ProcessedDecisionResult.Pending);

            Mocker.GetMock<IPendingReleaseService>().Verify(s => s.Add(decision, PendingReleaseReason.Delay), Times.Once());
            _remote.TargetQualityTrackIds.Should().Equal(20);
            Mocker.GetMock<IDownloadService>().Verify(s => s.DownloadReport(It.IsAny<RemoteEpisode>(), It.IsAny<int?>()), Times.Never());
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task should_send_selected_target_to_requested_client_or_persist_retry(bool unavailable)
        {
            var decision = new DownloadDecision(_remote);
            var service = Mocker.GetMock<IDownloadService>();

            if (unavailable)
            {
                service.Setup(s => s.DownloadReport(_remote, 7)).ThrowsAsync(new DownloadClientUnavailableException("Unavailable"));
            }
            else
            {
                service.Setup(s => s.DownloadReport(_remote, 7)).Returns(Task.CompletedTask);
            }

            (await Subject.ProcessDecision(decision, 7)).Should().Be(unavailable ? ProcessedDecisionResult.Failed : ProcessedDecisionResult.Grabbed);

            service.Verify(s => s.DownloadReport(_remote, 7), Times.Once());
            Mocker.GetMock<IPendingReleaseService>().Verify(s => s.Add(decision, PendingReleaseReason.DownloadClientUnavailable), unavailable ? Times.Once() : Times.Never());
            _remote.TargetQualityTrackIds.Should().Equal(20);
        }
    }
}
