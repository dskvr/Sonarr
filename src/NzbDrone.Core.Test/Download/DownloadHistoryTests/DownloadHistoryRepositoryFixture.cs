using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Download.DownloadHistoryTests
{
    [TestFixture]
    public class DownloadHistoryRepositoryFixture : DbTest<DownloadHistoryRepository, DownloadHistory>
    {
        private Series _series1;
        private Series _series2;

        [SetUp]
        public void Setup()
        {
            _series1 = Builder<Series>.CreateNew()
                                      .With(s => s.Id = 7)
                                      .Build();

            _series2 = Builder<Series>.CreateNew()
                                      .With(s => s.Id = 8)
                                      .Build();
        }

        [Test]
        public void should_persist_quality_track_ids_and_release_identity()
        {
            var history = new DownloadHistory
            {
                SeriesId = 7,
                DownloadId = "track-targets",
                SourceTitle = "Test.Release",
                Release = new ReleaseInfo { Guid = "release-guid", Title = "Test.Release", TargetQualityTrackIds = [10, 20] },
                Data = new Dictionary<string, string> { ["qualityTrackIds"] = "[10,20]" }
            };
            Subject.Insert(history);

            var reloaded = Subject.FindByDownloadId("track-targets").Single();

            QualityTrackSnapshot.ReadTargets(reloaded.Data).Should().Equal(10, 20);
            reloaded.Release.Guid.Should().Be("release-guid");
            reloaded.Release.TargetQualityTrackIds.Should().Equal(10, 20);
        }

        [Test]
        public void should_delete_history_items_by_seriesId()
        {
            var items = Builder<DownloadHistory>.CreateListOfSize(5)
                .TheFirst(1)
                .With(c => c.Id = 0)
                .With(c => c.SeriesId = _series2.Id)
                .TheRest()
                .With(c => c.Id = 0)
                .With(c => c.SeriesId = _series1.Id)
                .BuildListOfNew();

            Db.InsertMany(items);

            Subject.DeleteBySeriesIds(new List<int> { _series1.Id });

            var removedItems = Subject.All().Where(h => h.SeriesId == _series1.Id);
            var nonRemovedItems = Subject.All().Where(h => h.SeriesId == _series2.Id);

            removedItems.Should().HaveCount(0);
            nonRemovedItems.Should().HaveCount(1);
        }
    }
}
