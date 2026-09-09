using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.SeriesStats;
using NzbDrone.Core.Tv;
using NzbDrone.Core.Tv.Commands;
using NzbDrone.Test.Common;
using Sonarr.Api.V3.Series;

namespace NzbDrone.Api.Test.v3.Series
{
    [TestFixture]
    public class SeriesControllerQualityTracksFixture : TestBase<SeriesController>
    {
        private NzbDrone.Core.Tv.Series _series;

        [SetUp]
        public void Setup()
        {
            _series = new NzbDrone.Core.Tv.Series { Id = 1, QualityProfileId = 1, Path = "/series/original" };
            Mocker.GetMock<ISeriesService>().Setup(s => s.GetSeries(1)).Returns(_series);
            Mocker.GetMock<ISeriesStatisticsService>().Setup(s => s.SeriesStatistics(1, It.IsAny<int>())).Returns(new SeriesStatistics());
            Subject.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        }

        [Test]
        public void legacy_primary_update_should_preserve_extra_profile_request_state()
        {
            Subject.UpdateSeries(new SeriesResource { Id = 1, QualityProfileId = 3, Path = _series.Path });

            _series.QualityProfileId.Should().Be(3);
            _series.AdditionalQualityProfileIds.Should().BeNull();
            Mocker.GetMock<ISeriesQualityTrackService>().Verify(s => s.ValidateProfiles(1, 3, null), Times.Once());
        }

        [Test]
        public void legacy_conflicting_primary_update_should_not_start_move()
        {
            Mocker.GetMock<ISeriesQualityTrackService>().Setup(s => s.ValidateProfiles(1, 2, null))
                .Throws(new ValidationException(new[] { new ValidationFailure("AdditionalQualityProfileIds", "Duplicate profile") }));

            Assert.Throws<ValidationException>(() => Subject.UpdateSeries(new SeriesResource { Id = 1, QualityProfileId = 2, Path = "/series/new" }, true));

            _series.Path.Should().Be("/series/original");
            Mocker.GetMock<IManageCommandQueue>().Verify(s => s.Push(It.IsAny<MoveSeriesCommand>(), CommandPriority.Normal, CommandTrigger.Manual), Times.Never());
        }

        [Test]
        public void legacy_move_should_keep_source_path_until_worker_finishes()
        {
            var response = Subject.UpdateSeries(new SeriesResource { Id = 1, QualityProfileId = 3, Path = "/series/new" }, true);

            _series.Path.Should().Be("/series/original");
            _series.QualityProfileId.Should().Be(3);
            ((AcceptedAtActionResult)response.Result).Value.Should().BeOfType<SeriesResource>().Which.Path.Should().Be("/series/original");
            Mocker.GetMock<IManageCommandQueue>().Verify(s => s.Push(It.Is<MoveSeriesCommand>(c => c.SourcePath == "/series/original" && c.DestinationPath == "/series/new"), CommandPriority.Normal, CommandTrigger.Manual), Times.Once());
        }

        [Test]
        public void legacy_path_change_without_move_should_apply_immediately()
        {
            Subject.UpdateSeries(new SeriesResource { Id = 1, QualityProfileId = 1, Path = "/series/new" });

            _series.Path.Should().Be("/series/new");
            Mocker.GetMock<IManageCommandQueue>().Verify(s => s.Push(It.IsAny<MoveSeriesCommand>(), CommandPriority.Normal, CommandTrigger.Manual), Times.Never());
        }
    }
}
