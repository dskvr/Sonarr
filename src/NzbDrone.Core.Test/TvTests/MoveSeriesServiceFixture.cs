using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FizzWare.NBuilder;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Core.Tv.Commands;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.TvTests
{
    [TestFixture]
    public class MoveSeriesServiceFixture : CoreTest<MoveSeriesService>
    {
        private Series _series;
        private MoveSeriesCommand _command;
        private BulkMoveSeriesCommand _bulkCommand;

        [SetUp]
        public void Setup()
        {
            _series = Builder<Series>
                .CreateNew()
                .Build();

            _command = new MoveSeriesCommand
                       {
                           SeriesId = 1,
                           SourcePath = @"C:\Test\TV\Series".AsOsAgnostic(),
                           DestinationPath = @"C:\Test\TV2\Series".AsOsAgnostic()
                       };

            _bulkCommand = new BulkMoveSeriesCommand
                       {
                           Series = new List<BulkMoveSeries>
                                    {
                                        new BulkMoveSeries
                                        {
                                            SeriesId = 1,
                                            SourcePath = @"C:\Test\TV\Series".AsOsAgnostic()
                                        }
                                    },
                           DestinationRootFolder = @"C:\Test\TV2".AsOsAgnostic()
                       };

            _series.Path = _command.SourcePath;

            Mocker.GetMock<ISeriesService>()
                  .Setup(s => s.GetSeries(It.IsAny<int>()))
                  .Returns(_series);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FolderExists(It.IsAny<string>()))
                  .Returns(true);
        }

        private void GivenFailedMove()
        {
            Mocker.GetMock<ISeriesFolderMoveService>()
                  .Setup(s => s.Move(It.IsAny<Series>(), It.IsAny<string>(), It.IsAny<string>()))
                  .Throws<IOException>();
        }

        [Test]
        public void should_log_error_when_move_throws_an_exception()
        {
            GivenFailedMove();

            Assert.Throws<IOException>(() => Subject.Execute(_command));

            ExceptionVerification.ExpectedErrors(1);
        }

        [Test]
        public void should_not_rewrite_path_after_safe_move_failure()
        {
            GivenFailedMove();

            Assert.Throws<IOException>(() => Subject.Execute(_command));

            ExceptionVerification.ExpectedErrors(1);

            Mocker.GetMock<ISeriesService>()
                  .Verify(v => v.UpdateSeries(It.IsAny<Series>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void should_use_destination_path()
        {
            Subject.Execute(_command);

            Mocker.GetMock<ISeriesFolderMoveService>()
                  .Verify(v => v.Move(_series, _command.SourcePath, _command.DestinationPath), Times.Once());

            Mocker.GetMock<IBuildFileNames>()
                  .Verify(v => v.GetSeriesFolder(It.IsAny<Series>(), null), Times.Never());
        }

        [Test]
        public void should_build_new_path_when_root_folder_is_provided()
        {
            var seriesFolder = "Series";
            var expectedPath = Path.Combine(_bulkCommand.DestinationRootFolder, seriesFolder);

            Mocker.GetMock<IBuildFileNames>()
                    .Setup(s => s.GetSeriesFolder(It.IsAny<Series>(), null))
                    .Returns(seriesFolder);

            Subject.Execute(_bulkCommand);

            Mocker.GetMock<ISeriesFolderMoveService>()
                  .Verify(v => v.Move(_series, _bulkCommand.Series.First().SourcePath, expectedPath), Times.Once());
        }

        [Test]
        public void should_delegate_missing_folder_handling_to_safe_move_service()
        {
            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FolderExists(It.IsAny<string>()))
                  .Returns(false);

            Subject.Execute(_command);

            Mocker.GetMock<ISeriesFolderMoveService>()
                  .Verify(v => v.Move(_series, _command.SourcePath, _command.DestinationPath), Times.Once());

            Mocker.GetMock<IBuildFileNames>()
                  .Verify(v => v.GetSeriesFolder(It.IsAny<Series>(), null), Times.Never());
        }

        [Test]
        public void should_restore_legacy_prepublished_source_before_moving_files()
        {
            _series.Path = _command.DestinationPath;
            Subject.Execute(_command);
            Mocker.GetMock<ISeriesService>().Verify(s => s.UpdateSeries(It.Is<Series>(series => series.Path == _command.SourcePath), false, true), Times.Once());
            Mocker.GetMock<ISeriesFolderMoveService>().Verify(s => s.Move(_series, _command.SourcePath, _command.DestinationPath), Times.Once());
        }

        [Test]
        public void should_not_repeat_a_legacy_command_whose_files_already_moved()
        {
            _series.Path = _command.DestinationPath;
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FolderExists(_command.SourcePath)).Returns(false);
            Subject.Execute(_command);
            Mocker.GetMock<ISeriesFolderMoveService>().Verify(s => s.Move(It.IsAny<Series>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never());
            Mocker.GetMock<ISeriesService>().Verify(s => s.UpdateSeries(It.IsAny<Series>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void should_reject_legacy_command_when_both_folders_are_unavailable()
        {
            _series.Path = _command.DestinationPath;
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FolderExists(It.IsAny<string>())).Returns(false);
            Assert.Throws<IOException>(() => Subject.Execute(_command));
            ExceptionVerification.ExpectedErrors(1);
            Mocker.GetMock<ISeriesFolderMoveService>().Verify(s => s.Move(It.IsAny<Series>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void should_reject_command_for_a_series_that_changed_location()
        {
            _series.Path = Path.Combine(Path.GetDirectoryName(_command.SourcePath), "Other Location");
            Assert.Throws<IOException>(() => Subject.Execute(_command));
            ExceptionVerification.ExpectedErrors(1);
            Mocker.GetMock<ISeriesFolderMoveService>().Verify(s => s.Move(It.IsAny<Series>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void should_report_bulk_failure_after_attempting_remaining_series()
        {
            var other = _series.Clone();
            other.Id = 2;
            other.Path = Path.Combine(Path.GetDirectoryName(_command.SourcePath), "Other");
            _bulkCommand.Series.Add(new BulkMoveSeries { SeriesId = other.Id, SourcePath = other.Path });
            Mocker.GetMock<ISeriesService>().Setup(s => s.GetSeries(other.Id)).Returns(other);
            Mocker.GetMock<IBuildFileNames>().Setup(s => s.GetSeriesFolder(It.IsAny<Series>(), null)).Returns<Series, NamingConfig>((series, _) => series.Id == 1 ? "Series" : "Other");
            Mocker.GetMock<ISeriesFolderMoveService>().Setup(s => s.Move(_series, It.IsAny<string>(), It.IsAny<string>())).Throws(new IOException("First folder unavailable"));

            Assert.Throws<AggregateException>(() => Subject.Execute(_bulkCommand));

            ExceptionVerification.ExpectedErrors(1);
            Mocker.GetMock<ISeriesFolderMoveService>().Verify(s => s.Move(other, other.Path, Path.Combine(_bulkCommand.DestinationRootFolder, "Other")), Times.Once());
        }

        [Test]
        public void should_skip_invalid_source_without_attempting_disk_operations()
        {
            _command.SourcePath = "not-an-absolute-path";
            Subject.Execute(_command);
            ExceptionVerification.ExpectedWarns(1);
            Mocker.GetMock<ISeriesFolderMoveService>().Verify(s => s.Move(It.IsAny<Series>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void should_skip_command_when_source_and_destination_are_the_same()
        {
            _command.DestinationPath = _command.SourcePath;
            Subject.Execute(_command);
            Mocker.GetMock<ISeriesFolderMoveService>().Verify(s => s.Move(It.IsAny<Series>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never());
        }
    }
}
