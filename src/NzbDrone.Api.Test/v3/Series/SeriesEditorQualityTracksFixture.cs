using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Tv;
using NzbDrone.Core.Tv.Commands;
using NzbDrone.Test.Common;
using Sonarr.Api.V3.Series;

namespace NzbDrone.Api.Test.v3.Series
{
    [TestFixture]
    public class SeriesEditorQualityTracksFixture : TestBase<SeriesEditorController>
    {
        private NzbDrone.Core.Tv.Series _series;

        [SetUp]
        public void Setup()
        {
            _series = new NzbDrone.Core.Tv.Series
            {
                Id = 1,
                QualityProfileId = 1,
                QualityTracks = new List<SeriesQualityTrack>
                {
                    new SeriesQualityTrack { Id = 10, QualityProfileId = 1, IsPrimary = true, Enabled = true },
                    new SeriesQualityTrack { Id = 20, QualityProfileId = 2, Enabled = true }
                }
            };
            Mocker.GetMock<ISeriesService>().Setup(s => s.GetSeries(It.IsAny<IEnumerable<int>>())).Returns(new List<NzbDrone.Core.Tv.Series> { _series });
            Mocker.GetMock<ISeriesService>().Setup(s => s.UpdateSeries(It.IsAny<List<NzbDrone.Core.Tv.Series>>(), It.IsAny<bool>()))
                .Returns<List<NzbDrone.Core.Tv.Series>, bool>((series, _) => series);
            Mocker.GetMock<IQualityProfileService>().Setup(s => s.Exists(It.IsAny<int>())).Returns(true);
        }

        [Test]
        public void legacy_primary_update_should_preserve_secondary_configuration()
        {
            Subject.SaveAll(new SeriesEditorResource { SeriesIds = new List<int> { 1 }, QualityProfileId = 3 });

            _series.QualityProfileId.Should().Be(3);
            _series.AdditionalQualityProfileIds.Should().BeNull();
            _series.QualityTracks.Value.Should().Contain(t => t.Id == 20 && t.Enabled);
            Mocker.GetMock<ISeriesQualityTrackService>().Verify(s => s.ValidateProfiles(1, 3, null), Times.Once());
        }

        [Test]
        public void legacy_unrelated_edit_should_validate_preserved_profiles()
        {
            Subject.SaveAll(new SeriesEditorResource { SeriesIds = new List<int> { 1 }, Monitored = true });

            _series.Monitored.Should().BeTrue();
            _series.AdditionalQualityProfileIds.Should().BeNull();
            Mocker.GetMock<ISeriesQualityTrackService>().Verify(s => s.ValidateProfiles(1, 1, null), Times.Once());
        }

        [Test]
        public void legacy_primary_conflict_should_fail_before_persisting_any_changes()
        {
            Mocker.GetMock<ISeriesQualityTrackService>().Setup(s => s.ValidateProfiles(1, 2, null))
                .Throws(new ValidationException(new[] { new ValidationFailure("AdditionalQualityProfileIds", "Duplicate profile") }));

            Assert.Throws<ValidationException>(() => Subject.SaveAll(new SeriesEditorResource { SeriesIds = new List<int> { 1 }, QualityProfileId = 2, Monitored = true }));

            _series.QualityProfileId.Should().Be(1);
            _series.Monitored.Should().BeFalse();
            Mocker.GetMock<ISeriesService>().Verify(s => s.UpdateSeries(It.IsAny<List<NzbDrone.Core.Tv.Series>>(), It.IsAny<bool>()), Times.Never());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void legacy_bulk_move_should_defer_only_path_change(bool moveFiles)
        {
            var destination = @"C:\NewLibrary".AsOsAgnostic();
            _series.Path = @"C:\Original\Show".AsOsAgnostic();
            Mocker.GetMock<IRootFolderService>().Setup(s => s.All()).Returns(new List<RootFolder> { new RootFolder { Path = destination } });

            Subject.SaveAll(new SeriesEditorResource { SeriesIds = new List<int> { 1 }, QualityProfileId = 3, RootFolderPath = destination, MoveFiles = moveFiles });

            _series.QualityProfileId.Should().Be(3);
            _series.RootFolderPath.Should().Be(moveFiles ? null : destination);
            Mocker.GetMock<IManageCommandQueue>().Verify(s => s.Push(It.Is<BulkMoveSeriesCommand>(c => c.DestinationRootFolder == destination && c.Series.Single().SourcePath == _series.Path), CommandPriority.Normal, CommandTrigger.Unspecified), moveFiles ? Times.Once() : Times.Never());
        }
    }
}
