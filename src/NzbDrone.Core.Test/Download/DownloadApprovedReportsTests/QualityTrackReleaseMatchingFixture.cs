using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Download.DownloadApprovedReportsTests
{
    [TestFixture]
    public class QualityTrackReleaseMatchingFixture : CoreTest<ProcessDownloadDecisions>
    {
        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<IPrioritizeDownloadDecision>().Setup(s => s.PrioritizeDecisions(It.IsAny<List<DownloadDecision>>()))
                .Returns<List<DownloadDecision>>(decisions => decisions);
        }

        private DownloadDecision Decision(int target, string guid)
        {
            return new DownloadDecision(new RemoteEpisode
            {
                Series = new Series { Id = 1 },
                Episodes = [new Episode { Id = 1, SeriesId = 1 }],
                Release = new ReleaseInfo { Guid = guid, Title = "Release", IndexerId = 1 },
                TargetQualityTrackIds = [target],
                TargetQualityTrackSignatures = new Dictionary<int, string> { [target] = "signature-" + target }
            });
        }

        [TestCase("series")]
        [TestCase("indexer")]
        [TestCase("missing-guid")]
        public async Task should_not_merge_releases_without_matching_series_and_provider_identity(string difference)
        {
            var first = Decision(10, "same-guid");
            var second = Decision(20, "same-guid");
            if (difference == "series")
            {
                second.RemoteEpisode.Series.Id = 2;
                second.RemoteEpisode.Episodes = [new Episode { Id = 2, SeriesId = 2 }];
            }
            else if (difference == "indexer")
            {
                second.RemoteEpisode.Release.IndexerId = 2;
            }
            else
            {
                first.RemoteEpisode.Release.Guid = null;
                second.RemoteEpisode.Release.Guid = null;
            }

            var result = await Subject.ProcessDecisions([first, second]);

            result.Grabbed.Should().HaveCount(2);
            result.Grabbed.Select(r => r.RemoteEpisode.TargetQualityTrackIds.Single()).Should().Equal(10, 20);
            Mocker.GetMock<IDownloadService>().Verify(s => s.DownloadReport(It.IsAny<RemoteEpisode>(), null), Times.Exactly(2));
        }

        [Test]
        public async Task should_merge_equal_release_identity_after_independent_reconstruction()
        {
            var first = Decision(10, "same-guid");
            var second = Decision(20, "same-guid");

            var result = await Subject.ProcessDecisions([first, second]);

            result.Grabbed.Should().ContainSingle().Which.RemoteEpisode.TargetQualityTrackIds.Should().BeEquivalentTo(new[] { 10, 20 });
            result.Grabbed[0].RemoteEpisode.TargetQualityTrackSignatures.Keys.Should().BeEquivalentTo(new[] { 10, 20 });
            Mocker.GetMock<IDownloadService>().Verify(s => s.DownloadReport(It.IsAny<RemoteEpisode>(), null), Times.Once());
        }

        [Test]
        public async Task should_mark_fallback_only_for_the_same_pending_target()
        {
            var first = Decision(10, "first");
            var second = Decision(20, "second");
            var fallback = Decision(10, "fallback");
            var delayed = new[] { first, second, fallback }.Select(d => new DownloadDecision(
                d.RemoteEpisode,
                new DownloadRejection(DownloadRejectionReason.MinimumAgeDelay, "Delayed", RejectionType.Temporary))).ToList();
            List<Tuple<DownloadDecision, PendingReleaseReason>> persisted = null;
            Mocker.GetMock<IPendingReleaseService>().Setup(s => s.AddMany(It.IsAny<List<Tuple<DownloadDecision, PendingReleaseReason>>>()))
                .Callback<List<Tuple<DownloadDecision, PendingReleaseReason>>>(items => persisted = items);

            await Subject.ProcessDecisions(delayed);

            persisted.Select(p => p.Item2).Should().Equal(PendingReleaseReason.Delay, PendingReleaseReason.Delay, PendingReleaseReason.Fallback);
            persisted[1].Item1.RemoteEpisode.TargetQualityTrackIds.Should().Equal(20);
            Mocker.GetMock<IDownloadService>().Verify(s => s.DownloadReport(It.IsAny<RemoteEpisode>(), null), Times.Never());
        }
    }
}
