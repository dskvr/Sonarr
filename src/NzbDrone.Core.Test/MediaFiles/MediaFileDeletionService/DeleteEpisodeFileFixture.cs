using System.Collections.Generic;
using System.IO;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles.MediaFileDeletionService
{
    [TestFixture]
    public class DeleteEpisodeFileFixture : CoreTest<Core.MediaFiles.MediaFileDeletionService>
    {
        private const string ROOT_FOLDER = @"C:\Test\TV";
        private Series _series;
        private EpisodeFile _episodeFile;

        [SetUp]
        public void Setup()
        {
            _series = Builder<Series>.CreateNew()
                                     .With(s => s.Path = Path.Combine(ROOT_FOLDER, "Series Title"))
                                     .Build();

            _episodeFile = Builder<EpisodeFile>.CreateNew()
                                               .With(f => f.RelativePath = "Series Title - S01E01")
                                               .With(f => f.Path = Path.Combine(_series.Path, "Series Title - S01E01"))
                                               .Build();

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.GetParentFolder(_series.Path))
                  .Returns(ROOT_FOLDER);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.GetParentFolder(_episodeFile.Path))
                  .Returns(_series.Path);

            Mocker.GetMock<ISeriesService>().Setup(s => s.GetSeries(_series.Id)).Returns(_series);
            Mocker.GetMock<IMediaFileService>().Setup(s => s.Get(It.IsAny<IEnumerable<int>>())).Returns(new List<EpisodeFile> { _episodeFile });
        }

        private void GivenRootFolderExists()
        {
            Mocker.GetMock<IRootFolderService>()
                .Setup(s => s.GetBestRootFolderPath(_series.Path))
                .Returns(ROOT_FOLDER);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FolderExists(ROOT_FOLDER))
                  .Returns(true);
        }

        private void GivenRootFolderHasFolders()
        {
            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.GetDirectories(ROOT_FOLDER))
                  .Returns(new[] { _series.Path });
        }

        private void GivenSeriesFolderExists()
        {
            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FolderExists(_series.Path))
                  .Returns(true);
        }

        [Test]
        public void should_throw_if_root_folder_does_not_exist()
        {
            Assert.Throws<NzbDroneClientException>(() => Subject.DeleteEpisodeFile(_series, _episodeFile));
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_should_throw_if_root_folder_is_empty()
        {
            GivenRootFolderExists();

            Assert.Throws<NzbDroneClientException>(() => Subject.DeleteEpisodeFile(_series, _episodeFile));
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_delete_from_db_if_series_folder_does_not_exist()
        {
            GivenRootFolderExists();
            GivenRootFolderHasFolders();

            Subject.DeleteEpisodeFile(_series, _episodeFile);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Delete(_episodeFile, DeleteMediaFileReason.Manual), Times.Once());
            Mocker.GetMock<IRecycleBinProvider>().Verify(v => v.DeleteFile(_episodeFile.Path, It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void should_delete_from_db_if_episode_file_does_not_exist()
        {
            GivenRootFolderExists();
            GivenRootFolderHasFolders();
            GivenSeriesFolderExists();

            Subject.DeleteEpisodeFile(_series, _episodeFile);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Delete(_episodeFile, DeleteMediaFileReason.Manual), Times.Once());
            Mocker.GetMock<IRecycleBinProvider>().Verify(v => v.DeleteFile(_episodeFile.Path, It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void should_delete_from_disk_and_db_if_episode_file_exists()
        {
            GivenRootFolderExists();
            GivenRootFolderHasFolders();
            GivenSeriesFolderExists();

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FileExists(_episodeFile.Path))
                  .Returns(true);

            Subject.DeleteEpisodeFile(_series, _episodeFile);

            Mocker.GetMock<IRecycleBinProvider>().Verify(v => v.DeleteFile(_episodeFile.Path, "Series Title"), Times.Once());
            Mocker.GetMock<IMediaFileService>().Verify(v => v.Delete(_episodeFile, DeleteMediaFileReason.Manual), Times.Once());
        }

        [Test]
        public void should_handle_error_deleting_episode_file()
        {
            GivenRootFolderExists();
            GivenRootFolderHasFolders();
            GivenSeriesFolderExists();

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FileExists(_episodeFile.Path))
                  .Returns(true);

            Mocker.GetMock<IRecycleBinProvider>()
                  .Setup(s => s.DeleteFile(_episodeFile.Path, "Series Title"))
                  .Throws(new IOException());

            Assert.Throws<NzbDroneClientException>(() => Subject.DeleteEpisodeFile(_series, _episodeFile));

            ExceptionVerification.ExpectedErrors(1);
            Mocker.GetMock<IRecycleBinProvider>().Verify(v => v.DeleteFile(_episodeFile.Path, "Series Title"), Times.Once());
            Mocker.GetMock<IMediaFileService>().Verify(v => v.Delete(_episodeFile, DeleteMediaFileReason.Manual), Times.Never());
        }

        [Test]
        public void should_preserve_replacement_bytes_when_caller_holds_deleted_file_id()
        {
            var seriesPath = Path.Combine(TempFolder, "series");
            Directory.CreateDirectory(seriesPath);
            var replacementPath = Path.Combine(seriesPath, "episode.mkv");
            File.WriteAllText(replacementPath, "new version bytes");
            _series.Path = seriesPath;
            var staleFile = new EpisodeFile { Id = 42, SeriesId = _series.Id, RelativePath = "episode.mkv" };

            // The queued API request captured ID 42; an upgrade already replaced it with another ID at this path.
            Mocker.GetMock<IMediaFileService>().Setup(s => s.Get(It.IsAny<IEnumerable<int>>())).Returns(new List<EpisodeFile>());
            Mocker.GetMock<IRecycleBinProvider>().Setup(s => s.DeleteFile(It.IsAny<string>(), It.IsAny<string>())).Callback<string, string>((path, folder) => File.Delete(path));

            Subject.DeleteEpisodeFile(_series, staleFile);

            File.ReadAllText(replacementPath).Should().Be("new version bytes");
            Mocker.GetMock<IRecycleBinProvider>().Verify(s => s.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void should_reject_file_from_another_series_before_deleting_bytes()
        {
            _episodeFile.SeriesId = _series.Id + 1;

            Assert.Throws<NzbDroneClientException>(() => Subject.DeleteEpisodeFile(_series, _episodeFile));

            Mocker.GetMock<IRecycleBinProvider>().Verify(s => s.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Delete(It.IsAny<EpisodeFile>(), It.IsAny<DeleteMediaFileReason>()), Times.Never());
        }

        [Test]
        public void bulk_deletion_should_recover_then_delete_all_current_versions()
        {
            _series.Path = Path.Combine(TempFolder, "series");
            Directory.CreateDirectory(_series.Path);
            var recycle = Path.Combine(TempFolder, "recycle");
            Directory.CreateDirectory(recycle);
            var files = new List<EpisodeFile>
            {
                new EpisodeFile { Id = 1, SeriesId = _series.Id, RelativePath = "hd.mkv" },
                new EpisodeFile { Id = 2, SeriesId = _series.Id, RelativePath = "uhd.mkv" }
            };
            foreach (var file in files)
            {
                File.WriteAllText(Path.Combine(_series.Path, file.RelativePath), "version " + file.Id);
            }

            Mocker.GetMock<IDiskProvider>().Setup(s => s.FolderExists(It.IsAny<string>())).Returns<string>(Directory.Exists);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FileExists(It.IsAny<string>())).Returns<string>(File.Exists);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.GetDirectories(It.IsAny<string>())).Returns<string>(Directory.GetDirectories);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.GetParentFolder(It.IsAny<string>())).Returns<string>(Path.GetDirectoryName);
            Mocker.GetMock<IRootFolderService>().Setup(s => s.GetBestRootFolderPath(_series.Path)).Returns(TempFolder);
            Mocker.GetMock<IUpgradeMediaFiles>().Setup(s => s.RecoverFileOperations(_series)).Callback(() =>
            {
                File.Move(Path.Combine(_series.Path, "hd.mkv"), Path.Combine(_series.Path, "recovered-hd.mkv"));
                files[0].RelativePath = "recovered-hd.mkv";
            });
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFilesBySeries(_series.Id)).Returns(() => files.ToList());
            Mocker.GetMock<IMediaFileService>().Setup(s => s.Delete(It.IsAny<EpisodeFile>(), DeleteMediaFileReason.Manual)).Callback<EpisodeFile, DeleteMediaFileReason>((file, reason) => files.Remove(file));
            Mocker.GetMock<IRecycleBinProvider>().Setup(s => s.DeleteFile(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>((path, subfolder) =>
            {
                var destination = Path.Combine(recycle, Path.GetFileName(path));
                File.Move(path, destination);
                return destination;
            });

            Subject.Execute(new DeleteSeriesFilesCommand { SeriesIds = new List<int> { _series.Id } });

            files.Should().BeEmpty();
            Directory.GetFiles(_series.Path).Should().BeEmpty();
            File.ReadAllText(Path.Combine(recycle, "recovered-hd.mkv")).Should().Be("version 1");
            File.ReadAllText(Path.Combine(recycle, "uhd.mkv")).Should().Be("version 2");
        }
    }
}
