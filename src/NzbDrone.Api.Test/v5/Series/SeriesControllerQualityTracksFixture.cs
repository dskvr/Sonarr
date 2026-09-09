using System;
using System.Collections.Generic;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.SeriesStats;
using NzbDrone.Core.Tv;
using NzbDrone.Core.Tv.Commands;
using NzbDrone.Test.Common;
using Sonarr.Api.V5.Series;

namespace NzbDrone.Api.Test.v5.Series
{
    [TestFixture]
    public class SeriesControllerQualityTracksFixture : TestBase<SeriesController>
    {
        private NzbDrone.Core.Tv.Series _series;

        [SetUp]
        public void Setup()
        {
            _series = new NzbDrone.Core.Tv.Series
            {
                Id = 1,
                QualityProfileId = 1,
                Path = "/series/original",
                QualityTracks = new List<SeriesQualityTrack>
                {
                    new SeriesQualityTrack { Id = 10, SeriesId = 1, QualityProfileId = 1, IsPrimary = true, Enabled = true },
                    new SeriesQualityTrack { Id = 20, SeriesId = 1, QualityProfileId = 2, Enabled = true }
                }
            };
            Mocker.GetMock<ISeriesService>().Setup(s => s.GetSeries(1)).Returns(_series);
            Mocker.GetMock<ISeriesService>().Setup(s => s.GetAllSeries()).Returns(new List<NzbDrone.Core.Tv.Series> { _series });
            Mocker.GetMock<ISeriesStatisticsService>().Setup(s => s.SeriesStatistics()).Returns(new List<SeriesStatistics>());
            Mocker.GetMock<ISeriesStatisticsService>().Setup(s => s.SeriesStatistics(1, It.IsAny<int>())).Returns(new SeriesStatistics());
            Subject.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            Subject.Url = new Mock<IUrlHelper>().Object;
        }

        [Test]
        public void list_should_use_aggregated_counts_without_reading_all_file_links()
        {
            var tracks = _series.QualityTracks.Value;
            tracks[1].Enabled = false;
            Mocker.GetMock<ISeriesQualityTrackService>().Setup(s => s.GetAllTracks()).Returns(tracks);
            Mocker.GetMock<IEpisodeTrackFileService>().Setup(s => s.GetFileCountsByTrack()).Returns(new Dictionary<int, int> { [20] = 2 });

            var result = Subject.AllSeries(null, Array.Empty<SeriesSubresource>()).Value;

            result[0].AdditionalQualityProfileIds.Should().BeEmpty();
            result[0].QualityTracks.Should().ContainSingle(t => t.Id == 20 && !t.Enabled && t.EpisodeFileCount == 2);
            Mocker.GetMock<IEpisodeTrackFileService>().Verify(s => s.GetAll(), Times.Never());
        }

        [Test]
        public void add_should_forward_selected_additional_profiles()
        {
            Mocker.GetMock<IAddSeriesService>().Setup(s => s.AddSeries(It.IsAny<NzbDrone.Core.Tv.Series>())).Returns(_series);
            var request = new SeriesResource { QualityProfileId = 1, AdditionalQualityProfileIds = new List<int> { 2 } };

            Subject.AddSeries(request);

            Mocker.GetMock<ISeriesQualityTrackService>().Verify(s => s.ValidateProfiles(0, 1, request.AdditionalQualityProfileIds), Times.Once());
            Mocker.GetMock<IAddSeriesService>().Verify(s => s.AddSeries(It.Is<NzbDrone.Core.Tv.Series>(series => series.AdditionalQualityProfileIds.Count == 1 && series.AdditionalQualityProfileIds[0] == 2)), Times.Once());
        }

        [Test]
        public void invalid_add_should_fail_before_fetching_or_adding_series()
        {
            Mocker.GetMock<ISeriesQualityTrackService>().Setup(s => s.ValidateProfiles(0, 1, It.IsAny<IEnumerable<int>>()))
                .Throws(new ValidationException(new[] { new ValidationFailure("AdditionalQualityProfileIds", "Unknown profile") }));

            Assert.Throws<ValidationException>(() => Subject.AddSeries(new SeriesResource { QualityProfileId = 1, AdditionalQualityProfileIds = new List<int> { 99 } }));

            Mocker.GetMock<IAddSeriesService>().Verify(s => s.AddSeries(It.IsAny<NzbDrone.Core.Tv.Series>()), Times.Never());
        }

        [Test]
        public void omitted_profiles_update_should_preserve_additional_configuration()
        {
            Subject.UpdateSeries(new SeriesResource { Id = 1, QualityProfileId = 3, Path = _series.Path });

            _series.AdditionalQualityProfileIds.Should().BeNull();
            _series.QualityTracks.Value.Should().Contain(t => t.Id == 20 && t.Enabled);
            Mocker.GetMock<ISeriesQualityTrackService>().Verify(s => s.ValidateProfiles(1, 3, null), Times.Once());
        }

        [Test]
        public void invalid_update_should_fail_before_queueing_move()
        {
            Mocker.GetMock<ISeriesQualityTrackService>().Setup(s => s.ValidateProfiles(1, 2, null))
                .Throws(new ValidationException(new[] { new ValidationFailure("AdditionalQualityProfileIds", "Duplicate profile") }));

            Assert.Throws<ValidationException>(() => Subject.UpdateSeries(new SeriesResource { Id = 1, QualityProfileId = 2, Path = "/series/new" }, true));

            _series.Path.Should().Be("/series/original");
            Mocker.GetMock<ISeriesService>().Verify(s => s.UpdateSeries(It.IsAny<NzbDrone.Core.Tv.Series>(), true, true), Times.Never());
            Mocker.GetMock<IManageCommandQueue>().Verify(s => s.Push(It.IsAny<MoveSeriesCommand>(), CommandPriority.Normal, CommandTrigger.Manual), Times.Never());
        }

        [Test]
        public void successful_update_should_queue_move_after_configuration_saved()
        {
            var persisted = false;
            Mocker.GetMock<ISeriesService>().Setup(s => s.UpdateSeries(It.IsAny<NzbDrone.Core.Tv.Series>(), true, true))
                .Callback(() => persisted = true);
            Mocker.GetMock<IManageCommandQueue>().Setup(s => s.Push(It.IsAny<MoveSeriesCommand>(), CommandPriority.Normal, CommandTrigger.Manual))
                .Callback(() => persisted.Should().BeTrue());

            var response = Subject.UpdateSeries(new SeriesResource { Id = 1, QualityProfileId = 3, Path = "/series/new" }, true);

            Mocker.GetMock<IManageCommandQueue>().Verify(s => s.Push(It.Is<MoveSeriesCommand>(c => c.SourcePath == "/series/original" && c.DestinationPath == "/series/new"), CommandPriority.Normal, CommandTrigger.Manual), Times.Once());
            _series.Path.Should().Be("/series/original");
            _series.QualityProfileId.Should().Be(3);
            ((Microsoft.AspNetCore.Http.HttpResults.Accepted<SeriesResource>)response.Result).Value.Path.Should().Be("/series/original");
        }

        [Test]
        public void path_change_without_move_should_apply_immediately()
        {
            Subject.UpdateSeries(new SeriesResource { Id = 1, QualityProfileId = 1, Path = "/series/new" });

            _series.Path.Should().Be("/series/new");
            Mocker.GetMock<IManageCommandQueue>().Verify(s => s.Push(It.IsAny<MoveSeriesCommand>(), CommandPriority.Normal, CommandTrigger.Manual), Times.Never());
        }
    }
}
