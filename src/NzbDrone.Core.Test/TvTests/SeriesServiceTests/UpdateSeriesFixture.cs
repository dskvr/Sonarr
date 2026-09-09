using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FizzWare.NBuilder;
using FluentAssertions;
using FluentValidation;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.AutoTagging;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.TvTests.SeriesServiceTests
{
    [TestFixture]
    public class UpdateSeriesFixture : CoreTest<SeriesService>
    {
        private Series _fakeSeries;
        private Series _existingSeries;

        [SetUp]
        public void Setup()
        {
            _fakeSeries = Builder<Series>.CreateNew().Build();
            _existingSeries = Builder<Series>.CreateNew().Build();

            _fakeSeries.Seasons = new List<Season>
            {
                new Season { SeasonNumber = 1, Monitored = true },
                new Season { SeasonNumber = 2, Monitored = true }
            };

            _existingSeries.Seasons = new List<Season>
            {
                new Season { SeasonNumber = 1, Monitored = true },
                new Season { SeasonNumber = 2, Monitored = true }
            };

            Mocker.GetMock<IAutoTaggingService>()
                .Setup(s => s.GetTagChanges(It.IsAny<Series>()))
                .Returns(new AutoTaggingChanges());

            Mocker.GetMock<ISeriesRepository>()
                .Setup(s => s.Update(It.IsAny<Series>()))
                .Returns<Series>(r => r);
        }

        private void GivenExistingSeries()
        {
            Mocker.GetMock<ISeriesRepository>()
                  .Setup(s => s.Get(It.IsAny<int>()))
                  .Returns(_existingSeries);
        }

        [Test]
        public void should_not_update_episodes_if_season_hasnt_changed()
        {
            GivenExistingSeries();

            Subject.UpdateSeries(_fakeSeries);

            Mocker.GetMock<IEpisodeService>()
                  .Verify(v => v.SetEpisodeMonitoredBySeason(_fakeSeries.Id, It.IsAny<int>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void should_update_series_when_it_changes()
        {
            GivenExistingSeries();
            var seasonNumber = 1;
            var monitored = false;

            _fakeSeries.Seasons.Single(s => s.SeasonNumber == seasonNumber).Monitored = monitored;

            Subject.UpdateSeries(_fakeSeries);

            Mocker.GetMock<IEpisodeService>()
                  .Verify(v => v.SetEpisodeMonitoredBySeason(_fakeSeries.Id, seasonNumber, monitored), Times.Once());

            Mocker.GetMock<IEpisodeService>()
                  .Verify(v => v.SetEpisodeMonitoredBySeason(_fakeSeries.Id, It.IsAny<int>(), It.IsAny<bool>()), Times.Once());
        }

        [Test]
        public void should_add_and_remove_tags()
        {
            GivenExistingSeries();
            var seasonNumber = 1;
            var monitored = false;

            _fakeSeries.Tags = new HashSet<int> { 1, 2 };
            _fakeSeries.Seasons.Single(s => s.SeasonNumber == seasonNumber).Monitored = monitored;

            Mocker.GetMock<IAutoTaggingService>()
                .Setup(s => s.GetTagChanges(_fakeSeries))
                .Returns(new AutoTaggingChanges
                {
                    TagsToAdd = new HashSet<int> { 3 },
                    TagsToRemove = new HashSet<int> { 1 }
                });

            var result = Subject.UpdateSeries(_fakeSeries);

            result.Tags.Should().BeEquivalentTo(new[] { 2, 3 });
        }

        [Test]
        public void should_reject_invalid_profiles_before_changing_episode_monitoring()
        {
            GivenExistingSeries();
            _fakeSeries.Seasons[0].Monitored = false;
            Mocker.GetMock<ISeriesQualityTrackService>()
                .Setup(s => s.ValidateProfiles(_fakeSeries.Id, _fakeSeries.QualityProfileId, _fakeSeries.AdditionalQualityProfileIds))
                .Throws(new ValidationException("Invalid quality profile"));

            Assert.Throws<ValidationException>(() => Subject.UpdateSeries(_fakeSeries));

            Mocker.GetMock<IEpisodeService>().Verify(s => s.SetEpisodeMonitoredBySeason(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()), Times.Never());
            Mocker.GetMock<ISeriesRepository>().Verify(s => s.Update(It.IsAny<Series>()), Times.Never());
        }

        [Test]
        public void should_preserve_current_path_and_profiles_during_metadata_update()
        {
            GivenExistingSeries();
            _existingSeries.Path = "/library/moved";
            _existingSeries.QualityProfileId = 8;
            _fakeSeries.Path = "/library/previous";
            _fakeSeries.RootFolderPath = "/library/previous-root";
            _fakeSeries.QualityProfileId = 1;
            _fakeSeries.AdditionalQualityProfileIds = new List<int> { 2 };
            _fakeSeries.Tags = new HashSet<int> { 9 };
            _fakeSeries.Seasons[0].Monitored = false;

            var result = Subject.UpdateSeriesMetadata(_fakeSeries);

            result.Path.Should().Be(_existingSeries.Path);
            result.QualityProfileId.Should().Be(8);
            result.AdditionalQualityProfileIds.Should().BeNull();
            result.RootFolderPath.Should().BeNull();
            result.Tags.Should().BeEquivalentTo(new[] { 9 });
            Mocker.GetMock<IEpisodeService>().Verify(s => s.SetEpisodeMonitoredBySeason(_fakeSeries.Id, 1, false), Times.Once());
        }

        [Test]
        public void should_not_take_unrelated_series_locks_for_metadata_updates()
        {
            GivenExistingSeries();
            var service = Subject;
            lock (MediaFileOperationLock.ForSeries(47))
            {
                var update = Task.Run(() => service.UpdateSeriesMetadata(_fakeSeries));
                update.Wait(System.TimeSpan.FromSeconds(5)).Should().BeTrue();
                update.GetAwaiter().GetResult().Path.Should().Be(_existingSeries.Path);
            }
        }
    }
}
