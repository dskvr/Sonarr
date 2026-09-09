using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    [TestFixture]
    public class QualityTrackSeasonSearchFixture : CoreTest<SeasonSearchService>
    {
        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void should_dispatch_only_requested_versions_from_season_results(bool explicitTarget, bool empty)
        {
            var release = new ReleaseInfo { Guid = "season", Title = "The.Office.S03" };
            var series = new Series { Id = 1 };
            var episode = new Episode { Id = 1, SeriesId = 1, SeasonNumber = 3 };
            var first = new DownloadDecision(new RemoteEpisode { Series = series, Episodes = [episode], Release = release, TargetQualityTrackIds = [10] });
            var second = new DownloadDecision(new RemoteEpisode { Series = series, Episodes = [episode], Release = release, TargetQualityTrackIds = [20] });
            var aggregate = new DownloadDecision(new RemoteEpisode { Series = series, Episodes = [episode], Release = release })
            {
                QualityTrackDecisions = [first, second]
            };
            var downloads = new List<RemoteEpisode>();
            Mocker.GetMock<ISearchForReleases>().Setup(s => s.SeasonSearch(1, 3, false, true, true, false)).ReturnsAsync(new List<DownloadDecision> { aggregate });
            Mocker.GetMock<IPrioritizeDownloadDecision>().Setup(s => s.PrioritizeDecisions(It.IsAny<List<DownloadDecision>>()))
                .Returns<List<DownloadDecision>>(decisions => decisions);
            Mocker.GetMock<IDownloadService>().Setup(s => s.DownloadReport(It.IsAny<RemoteEpisode>(), null))
                .Callback<RemoteEpisode, int?>((remote, _) => downloads.Add(remote)).Returns(Task.CompletedTask);
            Mocker.SetConstant<IProcessDownloadDecisions>(Mocker.Resolve<ProcessDownloadDecisions>());

            Subject.Execute(new SeasonSearchCommand
            {
                SeriesId = 1,
                SeasonNumber = 3,
                Trigger = CommandTrigger.Manual,
                TargetQualityTrackIds = explicitTarget ? (empty ? [] : [20]) : null
            });

            if (empty)
            {
                downloads.Should().BeEmpty();
            }
            else
            {
                downloads.Should().ContainSingle().Which.TargetQualityTrackIds.Should().BeEquivalentTo(explicitTarget ? new[] { 20 } : new[] { 10, 20 });
            }
        }
    }
}
