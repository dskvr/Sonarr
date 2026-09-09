using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Extras;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles
{
    public class RenameEpisodeFileServiceFixture : CoreTest<RenameEpisodeFileService>
    {
        private Series _series;
        private List<EpisodeFile> _episodeFiles;

        [SetUp]
        public void Setup()
        {
            _series = Builder<Series>.CreateNew()
                                     .With(series => series.Path = Path.Combine(TempFolder, "series"))
                                     .Build();

            _episodeFiles = Builder<EpisodeFile>.CreateListOfSize(2)
                                                .All()
                                                .With(e => e.SeriesId = _series.Id)
                                                .With(e => e.SeasonNumber = 1)
                                                .Build()
                                                .ToList();

            Mocker.GetMock<ISeriesService>()
                  .Setup(s => s.GetSeries(_series.Id))
                  .Returns(_series);
            Mocker.GetMock<IEpisodeService>().Setup(service => service.GetEpisodeBySeries(_series.Id))
                .Returns(() => _episodeFiles.Select(file => new Episode { Id = file.Id, SeriesId = _series.Id, SeasonNumber = 1, EpisodeNumber = file.Id, EpisodeFileId = file.Id }).ToList());
            Mocker.GetMock<IBuildFileNames>()
                .Setup(service => service.BuildFilePath(It.IsAny<List<Episode>>(), _series, It.IsAny<EpisodeFile>(), It.IsAny<string>(), null, null))
                .Returns<List<Episode>, Series, EpisodeFile, string, NamingConfig, List<NzbDrone.Core.CustomFormats.CustomFormat>>((episodes, series, file, extension, config, formats) =>
                    Path.Combine(series.Path, "renamed-" + file.Id + ".mkv"));
        }

        private void GivenNoEpisodeFiles()
        {
            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.Get(It.IsAny<IEnumerable<int>>()))
                  .Returns(new List<EpisodeFile>());
        }

        private void GivenEpisodeFiles()
        {
            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.Get(It.IsAny<IEnumerable<int>>()))
                  .Returns(_episodeFiles);
        }

        private void GivenMovedFiles()
        {
            Mocker.GetMock<IMoveEpisodeFiles>()
                  .Setup(s => s.MoveEpisodeFile(It.IsAny<EpisodeFile>(), _series));
        }

        [Test]
        public void should_not_publish_event_if_no_files_to_rename()
        {
            GivenNoEpisodeFiles();

            Subject.Execute(new RenameFilesCommand(_series.Id, new List<int> { 1 }));

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<SeriesRenamedEvent>()), Times.Never());
        }

        [Test]
        public void should_not_publish_event_if_no_files_are_renamed()
        {
            GivenEpisodeFiles();

            Mocker.GetMock<IMoveEpisodeFiles>()
                  .Setup(s => s.MoveEpisodeFile(It.IsAny<EpisodeFile>(), It.IsAny<Series>()))
                  .Throws(new SameFilenameException("Same file name", "Filename"));

            Subject.Execute(new RenameFilesCommand(_series.Id, new List<int> { 1 }));

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<SeriesRenamedEvent>()), Times.Never());
        }

        [Test]
        public void should_publish_event_if_files_are_renamed()
        {
            GivenEpisodeFiles();
            GivenMovedFiles();

            Subject.Execute(new RenameFilesCommand(_series.Id, new List<int> { 1 }));

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<SeriesRenamedEvent>()), Times.Once());
        }

        [Test]
        public void should_update_moved_files()
        {
            GivenEpisodeFiles();
            GivenMovedFiles();

            Subject.Execute(new RenameFilesCommand(_series.Id, new List<int> { 1 }));

            Mocker.GetMock<IMediaFileService>()
                  .Verify(v => v.Update(It.IsAny<EpisodeFile>()), Times.Exactly(2));
        }

        [Test]
        public void should_get_episodefiles_by_ids_only()
        {
            GivenEpisodeFiles();
            GivenMovedFiles();

            var files = new List<int> { 1 };

            Subject.Execute(new RenameFilesCommand(_series.Id, files));

            Mocker.GetMock<IMediaFileService>()
                  .Verify(v => v.Get(files), Times.Once());
        }

        [Test]
        public void should_reject_cross_series_selection_before_moving_any_bytes()
        {
            _series.Path = Path.Combine(TempFolder, "series-a");
            var otherSeriesPath = Path.Combine(TempFolder, "series-b");
            Directory.CreateDirectory(_series.Path);
            Directory.CreateDirectory(otherSeriesPath);
            var firstPath = Path.Combine(_series.Path, "episode.mkv");
            var secondPath = Path.Combine(otherSeriesPath, "episode.mkv");
            File.WriteAllText(firstPath, "series A bytes");
            File.WriteAllText(secondPath, "series B bytes");
            _episodeFiles[0].RelativePath = "episode.mkv";
            _episodeFiles[1].RelativePath = "episode.mkv";
            _episodeFiles[1].SeriesId = _series.Id + 1;
            GivenEpisodeFiles();

            Assert.Throws<InvalidOperationException>(() => Subject.Execute(new RenameFilesCommand(_series.Id, [1, 2])));

            File.ReadAllText(firstPath).Should().Be("series A bytes");
            File.ReadAllText(secondPath).Should().Be("series B bytes");
            _episodeFiles.Should().OnlyContain(file => file.RelativePath == "episode.mkv");
            Mocker.GetMock<IMoveEpisodeFiles>().Verify(s => s.MoveEpisodeFile(It.IsAny<EpisodeFile>(), It.IsAny<Series>()), Times.Never());
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Update(It.IsAny<EpisodeFile>()), Times.Never());
        }

        private List<Episode> GivenPreviewFiles()
        {
            _series.Path = Path.Combine(TempFolder, "series");
            Directory.CreateDirectory(_series.Path);
            _episodeFiles[0].RelativePath = "old-hd.mkv";
            _episodeFiles[1].RelativePath = "old-uhd.mp4";
            var episodes = new List<Episode>
            {
                new Episode { Id = 10, SeriesId = _series.Id, SeasonNumber = 1, EpisodeNumber = 1, EpisodeFileId = _episodeFiles[0].Id },
                new Episode { Id = 11, SeriesId = _series.Id, SeasonNumber = 1, EpisodeNumber = 2 }
            };
            _episodeFiles[0].TrackFiles = null;
            _episodeFiles[1].TrackFiles = new List<EpisodeTrackFile>
            {
                new EpisodeTrackFile { EpisodeId = 10, TrackId = 2, EpisodeFileId = _episodeFiles[1].Id }
            };
            foreach (var file in _episodeFiles)
            {
                File.WriteAllText(Path.Combine(_series.Path, file.RelativePath), "bytes for " + file.Id);
            }

            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFilesBySeries(_series.Id)).Returns(_episodeFiles);
            Mocker.GetMock<IEpisodeService>().Setup(s => s.GetEpisodeBySeries(_series.Id)).Returns(episodes);
            Mocker.GetMock<IBuildFileNames>()
                .Setup(s => s.BuildFilePath(It.IsAny<List<Episode>>(), _series, It.IsAny<EpisodeFile>(), It.IsAny<string>(), null, null))
                .Returns<List<Episode>, Series, EpisodeFile, string, NamingConfig, List<NzbDrone.Core.CustomFormats.CustomFormat>>((items, series, file, extension, config, formats) =>
                    Path.Combine(series.Path, file.Id == _episodeFiles[0].Id ? "new-hd.mkv" : "old-hd.mp4"));
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FileExists(It.IsAny<string>())).Returns<string>(File.Exists);
            return episodes;
        }

        [Test]
        public void preview_should_show_secondary_ownership_and_retained_stem_collision()
        {
            GivenPreviewFiles();

            var previews = Subject.GetRenamePreviews(_series.Id);

            previews.Should().HaveCount(2);
            previews.Single(p => p.EpisodeFileId == _episodeFiles[1].Id).EpisodeNumbers.Should().Equal(1);
            previews.Single(p => p.EpisodeFileId == _episodeFiles[1].Id).Error.Should().Contain("base filename");
            previews.Single(p => p.EpisodeFileId == _episodeFiles[0].Id).Error.Should().BeNull();
            File.ReadAllText(Path.Combine(_series.Path, "old-uhd.mp4")).Should().Be("bytes for " + _episodeFiles[1].Id);
        }

        [Test]
        public void preview_should_not_use_stale_scalar_ownership_when_links_are_empty()
        {
            GivenPreviewFiles();
            _episodeFiles[0].TrackFiles = new List<EpisodeTrackFile>();

            Subject.GetRenamePreviews(_series.Id).Should().ContainSingle().Which.EpisodeFileId.Should().Be(_episodeFiles[1].Id);

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void preview_should_report_untracked_destination_file_without_changing_bytes()
        {
            GivenPreviewFiles();
            var destination = Path.Combine(_series.Path, "new-hd.mkv");
            File.WriteAllText(destination, "untracked bytes");

            Subject.GetRenamePreviews(_series.Id).Single(p => p.EpisodeFileId == _episodeFiles[0].Id).Error.Should().NotBeNull();

            File.ReadAllText(destination).Should().Be("untracked bytes");
        }

        [Test]
        public void bulk_series_rename_should_process_all_physical_versions()
        {
            GivenPreviewFiles();
            Mocker.GetMock<IBuildFileNames>()
                .Setup(service => service.BuildFilePath(It.IsAny<List<Episode>>(), _series, It.IsAny<EpisodeFile>(), It.IsAny<string>(), null, null))
                .Returns<List<Episode>, Series, EpisodeFile, string, NamingConfig, List<NzbDrone.Core.CustomFormats.CustomFormat>>((episodes, series, file, extension, config, formats) =>
                    Path.Combine(series.Path, "renamed-" + file.Id + Path.GetExtension(file.RelativePath)));
            Mocker.GetMock<ISeriesService>().Setup(s => s.GetSeries(It.IsAny<IEnumerable<int>>())).Returns(new List<Series> { _series });
            Mocker.GetMock<IMoveEpisodeFiles>().Setup(s => s.MoveEpisodeFile(It.IsAny<EpisodeFile>(), _series)).Returns<EpisodeFile, Series>((file, series) =>
            {
                var oldPath = Path.Combine(series.Path, file.RelativePath);
                var newRelativePath = "renamed-" + file.Id + Path.GetExtension(file.RelativePath);
                File.Move(oldPath, Path.Combine(series.Path, newRelativePath));
                file.RelativePath = newRelativePath;
                return file;
            });

            Subject.Execute(new RenameSeriesCommand { SeriesIds = new List<int> { _series.Id } });

            _episodeFiles.Should().OnlyContain(file => File.Exists(Path.Combine(_series.Path, file.RelativePath)));
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Update(It.IsAny<EpisodeFile>()), Times.Exactly(2));
            Mocker.GetMock<IExtraService>().Verify(s => s.MoveFilesAfterRename(_series, It.IsAny<EpisodeFile>(), true), Times.Exactly(2));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void rename_failure_should_recover_only_uncommitted_media_and_retain_receipt(bool committed)
        {
            GivenPreviewFiles();
            _episodeFiles.RemoveAt(1);
            GivenEpisodeFiles();
            var file = _episodeFiles[0];
            var oldPath = Path.Combine(_series.Path, file.RelativePath);
            var newPath = Path.Combine(_series.Path, "new-hd.mkv");
            var receipt = Path.Combine(TempFolder, "rename-receipt");
            Mocker.GetMock<IUpgradeMediaFiles>().Setup(s => s.BeginRename(_series, file, It.IsAny<string>())).Returns(() =>
            {
                File.WriteAllText(receipt, "pending rename");
                return receipt;
            });
            Mocker.GetMock<IMoveEpisodeFiles>().Setup(s => s.MoveEpisodeFile(file, _series)).Returns(() =>
            {
                File.Move(oldPath, newPath);
                file.RelativePath = "new-hd.mkv";
                return file;
            });
            Mocker.GetMock<IUpgradeMediaFiles>().Setup(s => s.RecoverFileOperations(_series)).Callback(() =>
            {
                if (File.Exists(newPath))
                {
                    File.Move(newPath, oldPath);
                    file.RelativePath = "old-hd.mkv";
                }
            });
            if (committed)
            {
                Mocker.GetMock<IExtraService>().Setup(s => s.MoveFilesAfterRename(_series, file, true)).Throws(new IOException("Subtitle store unavailable"));
            }
            else
            {
                Mocker.GetMock<IMediaFileService>().Setup(s => s.Update(file)).Throws(new IOException("Metadata store unavailable"));
            }

            Subject.Execute(new RenameFilesCommand(_series.Id, new List<int> { file.Id }));

            File.ReadAllText(committed ? newPath : oldPath).Should().Be("bytes for " + file.Id);
            File.Exists(committed ? oldPath : newPath).Should().BeFalse();
            File.Exists(receipt).Should().BeTrue();
            Mocker.GetMock<IUpgradeMediaFiles>().Verify(s => s.FinishRename(It.IsAny<Series>(), It.IsAny<string>()), Times.Never());
            Mocker.GetMock<IUpgradeMediaFiles>().Verify(s => s.RecoverFileOperations(_series), Times.Exactly(committed ? 1 : 2));
            ExceptionVerification.ExpectedErrors(1);
        }

        [Test]
        public void naming_collision_should_leave_media_and_metadata_untouched()
        {
            GivenPreviewFiles();
            _episodeFiles.RemoveAt(1);
            GivenEpisodeFiles();
            Mocker.GetMock<IUpgradeMediaFiles>().Setup(s => s.BeginRename(_series, It.IsAny<EpisodeFile>(), It.IsAny<string>())).Throws(new DestinationAlreadyExistsException("retained version"));

            Subject.Execute(new RenameFilesCommand(_series.Id, new List<int> { _episodeFiles[0].Id }));

            File.ReadAllText(Path.Combine(_series.Path, "old-hd.mkv")).Should().Be("bytes for " + _episodeFiles[0].Id);
            Mocker.GetMock<IMoveEpisodeFiles>().Verify(s => s.MoveEpisodeFile(It.IsAny<EpisodeFile>(), _series), Times.Never());
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Update(It.IsAny<EpisodeFile>()), Times.Never());
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void real_naming_template_omitting_quality_should_mark_all_planned_versions_and_reject_batch()
        {
            var episodes = GivenPreviewFiles();
            episodes.ForEach(episode => episode.Title = "Episode " + episode.EpisodeNumber);
            GivenEpisodeFiles();
            _series.Title = "Test Show";
            _series.SeriesType = SeriesTypes.Standard;
            _series.SeasonFolder = false;
            _episodeFiles.ForEach(file =>
            {
                file.Quality = new QualityModel(Quality.HDTV1080p);
                file.MediaInfo = null;
            });
            var naming = NamingConfig.Default;
            naming.RenameEpisodes = true;
            naming.StandardEpisodeFormat = "{Series Title} - S{season:00}E{episode:00}";
            Mocker.GetMock<INamingConfigService>().Setup(service => service.GetConfig()).Returns(naming);
            Mocker.GetMock<IQualityDefinitionService>().Setup(service => service.Get(It.IsAny<Quality>())).Returns(new QualityDefinition { Title = "HDTV-1080p" });
            Mocker.SetConstant<IBuildFileNames>(Mocker.Resolve<FileNameBuilder>());

            var previews = Subject.GetRenamePreviews(_series.Id);

            previews.Should().HaveCount(2).And.OnlyContain(preview => preview.Error != null);
            previews.Select(preview => Path.GetFileNameWithoutExtension(preview.NewPath)).Distinct().Should().ContainSingle();
            Assert.Throws<DestinationAlreadyExistsException>(() => Subject.Execute(new RenameFilesCommand(_series.Id, _episodeFiles.Select(file => file.Id).ToList())));
            foreach (var file in _episodeFiles)
            {
                File.ReadAllText(Path.Combine(_series.Path, file.RelativePath)).Should().Be("bytes for " + file.Id);
            }

            Mocker.GetMock<IMoveEpisodeFiles>().Verify(service => service.MoveEpisodeFile(It.IsAny<EpisodeFile>(), It.IsAny<Series>()), Times.Never());
            Mocker.GetMock<IMediaFileService>().Verify(service => service.Update(It.IsAny<EpisodeFile>()), Times.Never());
        }

        [TestCase("shared.mp4")]
        [TestCase("Shared.mp4")]
        public void planned_same_stem_versions_should_be_rejected_before_either_file_moves(string secondTarget)
        {
            GivenPreviewFiles();
            GivenEpisodeFiles();
            Mocker.GetMock<IBuildFileNames>()
                .Setup(service => service.BuildFilePath(It.IsAny<List<Episode>>(), _series, It.IsAny<EpisodeFile>(), It.IsAny<string>(), null, null))
                .Returns<List<Episode>, Series, EpisodeFile, string, NamingConfig, List<NzbDrone.Core.CustomFormats.CustomFormat>>((episodes, series, file, extension, config, formats) =>
                    Path.Combine(series.Path, file.Id == _episodeFiles[0].Id ? "Shared.mkv" : secondTarget));

            Subject.GetRenamePreviews(_series.Id).Should().HaveCount(2).And.OnlyContain(preview => preview.Error != null);
            Assert.Throws<DestinationAlreadyExistsException>(() => Subject.Execute(new RenameFilesCommand(_series.Id, _episodeFiles.Select(file => file.Id).ToList())));

            Mocker.GetMock<IMoveEpisodeFiles>().Verify(service => service.MoveEpisodeFile(It.IsAny<EpisodeFile>(), It.IsAny<Series>()), Times.Never());
            Mocker.GetMock<IMediaFileService>().Verify(service => service.Update(It.IsAny<EpisodeFile>()), Times.Never());
            File.ReadAllText(Path.Combine(_series.Path, "old-hd.mkv")).Should().Be("bytes for " + _episodeFiles[0].Id);
            File.ReadAllText(Path.Combine(_series.Path, "old-uhd.mp4")).Should().Be("bytes for " + _episodeFiles[1].Id);
        }
    }
}
