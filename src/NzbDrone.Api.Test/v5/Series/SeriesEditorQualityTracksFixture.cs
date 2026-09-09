using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Tv;
using NzbDrone.Core.Tv.Commands;
using NzbDrone.Test.Common;
using Sonarr.Api.V5.Series;

namespace NzbDrone.Api.Test.v5.Series
{
    [TestFixture]
    public class SeriesEditorQualityTracksFixture : TestBase<SeriesEditorController>
    {
        private List<NzbDrone.Core.Tv.Series> _series;

        [SetUp]
        public void Setup()
        {
            _series = new List<NzbDrone.Core.Tv.Series>
            {
                new NzbDrone.Core.Tv.Series
                {
                    Id = 1,
                    QualityProfileId = 1,
                    QualityTracks = new List<SeriesQualityTrack>
                    {
                        new SeriesQualityTrack { Id = 10, QualityProfileId = 1, IsPrimary = true, Enabled = true },
                        new SeriesQualityTrack { Id = 20, QualityProfileId = 2, Enabled = true },
                        new SeriesQualityTrack { Id = 30, QualityProfileId = 3, Enabled = false }
                    }
                },
                new NzbDrone.Core.Tv.Series
                {
                    Id = 2,
                    QualityProfileId = 2,
                    QualityTracks = new List<SeriesQualityTrack>
                    {
                        new SeriesQualityTrack { Id = 40, QualityProfileId = 2, IsPrimary = true, Enabled = true }
                    }
                }
            };

            Mocker.GetMock<IQualityProfileService>().Setup(s => s.Exists(It.IsAny<int>())).Returns(true);
            Mocker.GetMock<ISeriesService>().Setup(s => s.GetSeries(It.IsAny<IEnumerable<int>>())).Returns(_series);
            Mocker.GetMock<ISeriesService>().Setup(s => s.UpdateSeries(It.IsAny<List<NzbDrone.Core.Tv.Series>>(), It.IsAny<bool>()))
                .Returns<List<NzbDrone.Core.Tv.Series>, bool>((series, _) => series);
        }

        [Test]
        public void ordinary_bulk_edit_should_not_change_additional_profiles()
        {
            Subject.SaveAll(STJson.Deserialize<SeriesEditorResource>("{\"seriesIds\":[1,2],\"monitored\":true}"));

            _series.Should().OnlyContain(s => s.Monitored && s.AdditionalQualityProfileIds == null);
            _series[0].QualityTracks.Value.Should().Contain(t => t.Id == 20 && t.Enabled);
        }

        [TestCase("add", new[] { 2, 3 }, new[] { 3 })]
        [TestCase("remove", new[] { 2 }, new int[0])]
        [TestCase("replace", new[] { 3 }, new[] { 3 })]
        public void bulk_operation_should_apply_to_additional_profiles_only(string operation, int[] firstExpected, int[] secondExpected)
        {
            var resource = STJson.Deserialize<SeriesEditorResource>("{\"seriesIds\":[1,2],\"additionalQualityProfileIds\":[3],\"applyAdditionalQualityProfiles\":\"" + operation + "\"}");

            Subject.SaveAll(resource);

            _series[0].AdditionalQualityProfileIds.Should().BeEquivalentTo(firstExpected);
            _series[1].AdditionalQualityProfileIds.Should().BeEquivalentTo(secondExpected);
            _series.Select(s => s.QualityProfileId).Should().Equal(1, 2);
            _series[0].QualityTracks.Value.Should().Contain(t => t.Id == 30 && !t.Enabled);
        }

        [Test]
        public void explicit_empty_replace_should_disable_all_extras()
        {
            Subject.SaveAll(new SeriesEditorResource
            {
                SeriesIds = new List<int> { 1, 2 },
                AdditionalQualityProfileIds = new List<int>(),
                ApplyAdditionalQualityProfiles = ApplyAdditionalQualityProfiles.Replace
            });

            _series.Should().OnlyContain(s => s.AdditionalQualityProfileIds.Count == 0);
        }

        [TestCase("{\"additionalQualityProfileIds\":[3]}")]
        [TestCase("{\"applyAdditionalQualityProfiles\":\"add\"}")]
        [TestCase("{\"additionalQualityProfileIds\":[3],\"applyAdditionalQualityProfiles\":99}")]
        [TestCase("{\"additionalQualityProfileIds\":[3,3],\"applyAdditionalQualityProfiles\":\"add\"}")]
        public void malformed_operation_should_fail_before_updating_any_series(string json)
        {
            var resource = STJson.Deserialize<SeriesEditorResource>(json);

            Assert.Throws<ValidationException>(() => Subject.SaveAll(resource)).Errors.Should().NotBeEmpty();

            Mocker.GetMock<ISeriesService>().Verify(s => s.UpdateSeries(It.IsAny<List<NzbDrone.Core.Tv.Series>>(), It.IsAny<bool>()), Times.Never());
        }

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(99)]
        public void unknown_profile_should_fail_even_when_removing_an_unselected_profile(int profileId)
        {
            Mocker.GetMock<IQualityProfileService>().Setup(s => s.Exists(99)).Returns(false);

            Assert.Throws<ValidationException>(() => Subject.SaveAll(new SeriesEditorResource
            {
                AdditionalQualityProfileIds = new List<int> { profileId },
                ApplyAdditionalQualityProfiles = ApplyAdditionalQualityProfiles.Remove
            }));

            Mocker.GetMock<ISeriesService>().Verify(s => s.UpdateSeries(It.IsAny<List<NzbDrone.Core.Tv.Series>>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void invalid_later_series_should_prevent_all_mutation_and_move_commands()
        {
            Mocker.GetMock<ISeriesQualityTrackService>()
                .Setup(s => s.ValidateProfiles(2, 3, It.IsAny<IEnumerable<int>>()))
                .Throws(new ValidationException(new[] { new ValidationFailure("AdditionalQualityProfileIds", "Duplicate profile") }));

            Assert.Throws<ValidationException>(() => Subject.SaveAll(new SeriesEditorResource
            {
                QualityProfileId = 3,
                Monitored = true,
                MoveFiles = true,
                RootFolderPath = "/series"
            }));

            _series.Select(s => s.QualityProfileId).Should().Equal(1, 2);
            _series.Should().OnlyContain(s => !s.Monitored);
            Mocker.GetMock<ISeriesService>().Verify(s => s.UpdateSeries(It.IsAny<List<NzbDrone.Core.Tv.Series>>(), It.IsAny<bool>()), Times.Never());
            Mocker.GetMock<IManageCommandQueue>().Verify(s => s.Push(It.IsAny<BulkMoveSeriesCommand>(), CommandPriority.Normal, CommandTrigger.Unspecified), Times.Never());
        }

        [Test]
        public void failed_persistence_should_not_queue_a_move()
        {
            var rootFolder = @"C:\Series".AsOsAgnostic();
            Mocker.GetMock<IRootFolderService>().Setup(s => s.All()).Returns(new List<RootFolder> { new RootFolder { Path = rootFolder } });
            Mocker.GetMock<ISeriesService>()
                .Setup(s => s.UpdateSeries(It.IsAny<List<NzbDrone.Core.Tv.Series>>(), false))
                .Throws(new InvalidOperationException("Database write failed"));

            Assert.Throws<InvalidOperationException>(() => Subject.SaveAll(new SeriesEditorResource { MoveFiles = true, RootFolderPath = rootFolder }));

            Mocker.GetMock<IManageCommandQueue>().Verify(s => s.Push(It.IsAny<BulkMoveSeriesCommand>(), CommandPriority.Normal, CommandTrigger.Unspecified), Times.Never());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void bulk_move_should_defer_only_path_change_until_worker_finishes(bool moveFiles)
        {
            var rootFolder = @"C:\NewLibrary".AsOsAgnostic();
            _series[0].Path = @"C:\Original\First".AsOsAgnostic();
            _series[1].Path = @"C:\Original\Second".AsOsAgnostic();
            Mocker.GetMock<IRootFolderService>().Setup(s => s.All()).Returns(new List<RootFolder> { new RootFolder { Path = rootFolder } });

            Subject.SaveAll(new SeriesEditorResource { QualityProfileId = 3, RootFolderPath = rootFolder, MoveFiles = moveFiles });

            _series.Should().OnlyContain(s => s.QualityProfileId == 3 && s.RootFolderPath == (moveFiles ? null : rootFolder));
            Mocker.GetMock<IManageCommandQueue>().Verify(s => s.Push(It.Is<BulkMoveSeriesCommand>(c => c.DestinationRootFolder == rootFolder && c.Series[0].SourcePath == _series[0].Path && c.Series[1].SourcePath == _series[1].Path), CommandPriority.Normal, CommandTrigger.Unspecified), moveFiles ? Times.Once() : Times.Never());
        }

        [Test]
        public void move_destination_should_still_be_validated_before_deferring_path_change()
        {
            Mocker.GetMock<IRootFolderService>().Setup(s => s.All()).Returns(new List<RootFolder>());

            Assert.Throws<ValidationException>(() => Subject.SaveAll(new SeriesEditorResource { RootFolderPath = @"C:\UnknownLibrary".AsOsAgnostic(), MoveFiles = true }));

            Mocker.GetMock<ISeriesService>().Verify(s => s.UpdateSeries(It.IsAny<List<NzbDrone.Core.Tv.Series>>(), It.IsAny<bool>()), Times.Never());
        }
    }
}
