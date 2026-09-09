using System.Collections.Generic;
using System.IO;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Core.Extras;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles.EpisodeImport
{
    [TestFixture]
    public class ImportApprovedEpisodesFixture : CoreTest<ImportApprovedEpisodes>
    {
        private List<ImportDecision> _rejectedDecisions;
        private List<ImportDecision> _approvedDecisions;

        private DownloadClientItem _downloadClientItem;

        [SetUp]
        public void Setup()
        {
            _rejectedDecisions = new List<ImportDecision>();
            _approvedDecisions = new List<ImportDecision>();

            var outputPath = @"C:\Test\Unsorted\TV\30.Rock.S01E01".AsOsAgnostic();

            var series = Builder<Series>.CreateNew()
                                        .With(e => e.QualityProfile = new QualityProfile { Items = Qualities.QualityFixture.GetDefaultQualities() })
                                        .With(s => s.Path = @"C:\Test\TV\30 Rock".AsOsAgnostic())
                                        .Build();

            var episodes = Builder<Episode>.CreateListOfSize(5)
                                           .Build();

            _rejectedDecisions.Add(new ImportDecision(new LocalEpisode(), new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")));
            _rejectedDecisions.Add(new ImportDecision(new LocalEpisode(), new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")));
            _rejectedDecisions.Add(new ImportDecision(new LocalEpisode(), new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")));
            _rejectedDecisions.ForEach(r => r.LocalEpisode.FileEpisodeInfo = new ParsedEpisodeInfo());

            foreach (var episode in episodes)
            {
                _approvedDecisions.Add(new ImportDecision(
                                           new LocalEpisode
                                               {
                                                   Series = series,
                                                   Episodes = new List<Episode> { episode },
                                                   Path = Path.Combine(series.Path, "30 Rock - S01E01 - Pilot.avi"),
                                                   Quality = new QualityModel(Quality.Bluray720p),
                                                   ReleaseGroup = "DRONE",
                                                   FileEpisodeInfo = new ParsedEpisodeInfo()
                                               }));
            }

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Setup(s => s.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), It.IsAny<LocalEpisode>(), It.IsAny<bool>()))
                  .Returns(new EpisodeFileMoveResult());

            Mocker.GetMock<ISeriesQualityTrackService>()
                .Setup(s => s.GetEnabledTracks(It.IsAny<int>()))
                .Returns(new List<SeriesQualityTrack> { new SeriesQualityTrack { Id = 1, IsPrimary = true, Enabled = true } });

            Mocker.GetMock<IEpisodeTrackFileService>()
                .Setup(s => s.GetForFile(It.IsAny<int>()))
                .Returns(new List<EpisodeTrackFile>());

            Mocker.GetMock<IHistoryService>()
                .Setup(x => x.FindByDownloadId(It.IsAny<string>()))
                .Returns(new List<EpisodeHistory>());

            _downloadClientItem = Builder<DownloadClientItem>.CreateNew()
                .With(d => d.OutputPath = new OsPath(outputPath))
                .Build();
        }

        private void GivenNewDownload()
        {
            _approvedDecisions.ForEach(a => a.LocalEpisode.Path = Path.Combine(_downloadClientItem.OutputPath.ToString(), Path.GetFileName(a.LocalEpisode.Path)));
        }

        private void GivenExistingFileOnDisk()
        {
            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.GetFilesWithRelativePath(It.IsAny<int>(), It.IsAny<string>()))
                  .Returns(new List<EpisodeFile>());
        }

        [Test]
        public void should_not_import_any_if_there_are_no_approved_decisions()
        {
            Subject.Import(_rejectedDecisions, false).Where(i => i.Result == ImportResultType.Imported).Should().BeEmpty();

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.IsAny<EpisodeFile>()), Times.Never());
        }

        [Test]
        public void should_import_each_approved()
        {
            GivenExistingFileOnDisk();

            Subject.Import(_approvedDecisions, false).Should().HaveCount(5);
        }

        [Test]
        public void should_only_import_approved()
        {
            GivenExistingFileOnDisk();

            var all = new List<ImportDecision>();
            all.AddRange(_rejectedDecisions);
            all.AddRange(_approvedDecisions);

            var result = Subject.Import(all, false);

            result.Should().HaveCount(all.Count);
            result.Where(i => i.Result == ImportResultType.Imported).Should().HaveCount(_approvedDecisions.Count);
        }

        [Test]
        public void should_only_import_each_episode_once()
        {
            GivenExistingFileOnDisk();

            var all = new List<ImportDecision>();
            all.AddRange(_approvedDecisions);
            all.Add(new ImportDecision(_approvedDecisions.First().LocalEpisode));

            var result = Subject.Import(all, false);

            result.Where(i => i.Result == ImportResultType.Imported).Should().HaveCount(_approvedDecisions.Count);
        }

        [Test]
        public void should_move_new_downloads()
        {
            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true);

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Verify(v => v.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), _approvedDecisions.First().LocalEpisode, false),
                          Times.Once());
        }

        [Test]
        public void should_import_same_episode_for_distinct_tracks()
        {
            GivenExistingFileOnDisk();
            var first = _approvedDecisions.First();
            first.LocalEpisode.TargetQualityTrackIds = [1];
            var secondEpisode = first.LocalEpisode.Clone();
            secondEpisode.Path += ".second.avi";
            secondEpisode.TargetQualityTrackIds = [2];

            var results = Subject.Import([first, new ImportDecision(secondEpisode)], false);

            results.Should().HaveCount(2).And.OnlyContain(r => r.Result == ImportResultType.Imported);
            Mocker.GetMock<IEpisodeTrackFileService>().Verify(s => s.ImportFile(It.IsAny<EpisodeFile>(), It.Is<List<EpisodeTrackFile>>(l => l.Single().TrackId == 1)), Times.Once());
            Mocker.GetMock<IEpisodeTrackFileService>().Verify(s => s.ImportFile(It.IsAny<EpisodeFile>(), It.Is<List<EpisodeTrackFile>>(l => l.Single().TrackId == 2)), Times.Once());
        }

        [Test]
        public void should_keep_unfulfilled_target_when_another_target_was_imported()
        {
            GivenExistingFileOnDisk();
            var first = _approvedDecisions.First();
            first.LocalEpisode.TargetQualityTrackIds = [1];
            first.LocalEpisode.Size = 100;
            var secondEpisode = first.LocalEpisode.Clone();
            secondEpisode.Size = 50;
            secondEpisode.TargetQualityTrackIds = [1, 2];

            var results = Subject.Import([first, new ImportDecision(secondEpisode)], false);

            results.Should().OnlyContain(r => r.Result == ImportResultType.Imported);
            secondEpisode.TargetQualityTrackIds.Should().Equal(2);
        }

        [Test]
        public void should_not_suppress_retry_after_failed_import()
        {
            GivenExistingFileOnDisk();
            var first = _approvedDecisions.First();
            first.LocalEpisode.Size = 100;
            var secondEpisode = first.LocalEpisode.Clone();
            secondEpisode.Size = 50;
            Mocker.GetMock<IEpisodeTrackFileService>()
                .SetupSequence(s => s.ImportFile(It.IsAny<EpisodeFile>(), It.IsAny<List<EpisodeTrackFile>>()))
                .Throws(new IOException("Database unavailable"))
                .Returns(new List<int>());

            var results = Subject.Import([first, new ImportDecision(secondEpisode)], false);

            results.Should().ContainSingle(r => r.Result == ImportResultType.Imported);
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_not_insert_file_twice_after_upgrade_transaction()
        {
            Mocker.GetMock<IUpgradeMediaFiles>()
                .Setup(s => s.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), It.IsAny<LocalEpisode>(), It.IsAny<bool>()))
                .Returns(new EpisodeFileMoveResult());

            Subject.Import([_approvedDecisions.First()], true).Should().ContainSingle(r => r.Result == ImportResultType.Imported);

            Mocker.GetMock<IMediaFileService>().Verify(s => s.Add(It.IsAny<EpisodeFile>()), Times.Never());
            Mocker.GetMock<IEventAggregator>().Verify(s => s.PublishEvent(It.IsAny<EpisodeFileAddedEvent>()), Times.Once());
        }

        [Test]
        public void should_publish_EpisodeImportedEvent_for_new_downloads()
        {
            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true);

            Mocker.GetMock<IEventAggregator>()
                .Verify(v => v.PublishEvent(It.IsAny<EpisodeImportedEvent>()), Times.Once());
        }

        [Test]
        public void should_not_move_existing_files()
        {
            GivenExistingFileOnDisk();

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, false);

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Verify(v => v.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), _approvedDecisions.First().LocalEpisode, false),
                          Times.Never());
        }

        [Test]
        public void should_import_larger_files_first()
        {
            GivenExistingFileOnDisk();

            var fileDecision = _approvedDecisions.First();
            fileDecision.LocalEpisode.Size = 1.Gigabytes();

            var sampleDecision = new ImportDecision(
                new LocalEpisode
                 {
                     Series = fileDecision.LocalEpisode.Series,
                     Episodes = new List<Episode> { fileDecision.LocalEpisode.Episodes.First() },
                     Path = @"C:\Test\TV\30 Rock\30 Rock - S01E01 - Pilot.avi".AsOsAgnostic(),
                     Quality = new QualityModel(Quality.Bluray720p),
                     Size = 80.Megabytes()
                 });

            var all = new List<ImportDecision>();
            all.Add(fileDecision);
            all.Add(sampleDecision);

            var results = Subject.Import(all, false);

            results.Should().HaveCount(all.Count);
            results.Should().ContainSingle(d => d.Result == ImportResultType.Imported);
            results.Should().ContainSingle(d => d.Result == ImportResultType.Imported && d.ImportDecision.LocalEpisode.Size == fileDecision.LocalEpisode.Size);
        }

        [Test]
        public void should_copy_when_cannot_move_files_downloads()
        {
            GivenNewDownload();
            _downloadClientItem.Title = "30.Rock.S01E01";
            _downloadClientItem.CanMoveFiles = false;

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, _downloadClientItem);

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Verify(v => v.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), _approvedDecisions.First().LocalEpisode, true), Times.Once());
        }

        [Test]
        public void should_use_override_importmode()
        {
            GivenNewDownload();
            _downloadClientItem.Title = "30.Rock.S01E01";
            _downloadClientItem.CanMoveFiles = false;

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, _downloadClientItem, ImportMode.Move);

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Verify(v => v.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), _approvedDecisions.First().LocalEpisode, false), Times.Once());
        }

        [Test]
        public void should_use_file_name_only_for_download_client_item_without_a_job_folder()
        {
            var fileName = "Series.Title.S01E01.720p.HDTV.x264-Sonarr.mkv";
            var path = Path.Combine(@"C:\Test\Unsorted\TV\".AsOsAgnostic(), fileName);

            _downloadClientItem.OutputPath = new OsPath(path);
            _approvedDecisions.First().LocalEpisode.Path = path;

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, _downloadClientItem);

            Mocker.GetMock<IUpgradeMediaFiles>().Verify(v => v.UpgradeEpisodeFile(It.Is<EpisodeFile>(c => c.OriginalFilePath == fileName), It.IsAny<LocalEpisode>(), It.IsAny<bool>()));
        }

        [Test]
        public void should_use_folder_and_file_name_only_for_download_client_item_with_a_job_folder()
        {
            var name = "Series.Title.S01E01.720p.HDTV.x264-Sonarr";
            var outputPath = Path.Combine(@"C:\Test\Unsorted\TV\".AsOsAgnostic(), name);

            _downloadClientItem.OutputPath = new OsPath(outputPath);
            _approvedDecisions.First().LocalEpisode.Path = Path.Combine(outputPath, name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, _downloadClientItem);

            Mocker.GetMock<IUpgradeMediaFiles>().Verify(v => v.UpgradeEpisodeFile(It.Is<EpisodeFile>(c => c.OriginalFilePath == $"{name}\\{name}.mkv".AsOsAgnostic()), It.IsAny<LocalEpisode>(), It.IsAny<bool>()));
        }

        [Test]
        public void should_include_intermediate_folders_for_download_client_item_with_a_job_folder()
        {
            var name = "Series.Title.S01E01.720p.HDTV.x264-Sonarr";
            var outputPath = Path.Combine(@"C:\Test\Unsorted\TV\".AsOsAgnostic(), name);

            _downloadClientItem.OutputPath = new OsPath(outputPath);
            _approvedDecisions.First().LocalEpisode.Path = Path.Combine(outputPath, "subfolder", name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, _downloadClientItem);

            Mocker.GetMock<IUpgradeMediaFiles>().Verify(v => v.UpgradeEpisodeFile(It.Is<EpisodeFile>(c => c.OriginalFilePath == $"{name}\\subfolder\\{name}.mkv".AsOsAgnostic()), It.IsAny<LocalEpisode>(), It.IsAny<bool>()));
        }

        [Test]
        public void should_use_folder_info_release_title_to_find_relative_path()
        {
            var name = "Series.Title.S01E01.720p.HDTV.x264-Sonarr";
            var outputPath = Path.Combine(@"C:\Test\Unsorted\TV\".AsOsAgnostic(), name);
            var localEpisode = _approvedDecisions.First().LocalEpisode;

            localEpisode.FolderEpisodeInfo = new ParsedEpisodeInfo { ReleaseTitle = name };
            localEpisode.Path = Path.Combine(outputPath, "subfolder", name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, null);

            Mocker.GetMock<IUpgradeMediaFiles>().Verify(v => v.UpgradeEpisodeFile(It.Is<EpisodeFile>(c => c.OriginalFilePath == $"{name}\\subfolder\\{name}.mkv".AsOsAgnostic()), It.IsAny<LocalEpisode>(), It.IsAny<bool>()));
        }

        [Test]
        public void should_get_relative_path_when_there_is_no_grandparent_windows()
        {
            WindowsOnly();

            var name = "Series.Title.S01E01.720p.HDTV.x264-Sonarr";
            var outputPath = @"C:\";
            var localEpisode = _approvedDecisions.First().LocalEpisode;

            localEpisode.FolderEpisodeInfo = new ParsedEpisodeInfo { ReleaseTitle = name };
            localEpisode.Path = Path.Combine(outputPath, name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, null);

            Mocker.GetMock<IUpgradeMediaFiles>().Verify(v => v.UpgradeEpisodeFile(It.Is<EpisodeFile>(c => c.OriginalFilePath == $"{name}.mkv".AsOsAgnostic()), It.IsAny<LocalEpisode>(), It.IsAny<bool>()));
        }

        [Test]
        public void should_get_relative_path_when_there_is_no_grandparent_mono()
        {
            PosixOnly();

            var name = "Series.Title.S01E01.720p.HDTV.x264-Sonarr";
            var outputPath = "/";
            var localEpisode = _approvedDecisions.First().LocalEpisode;

            localEpisode.FolderEpisodeInfo = new ParsedEpisodeInfo { ReleaseTitle = name };
            localEpisode.Path = Path.Combine(outputPath, name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, null);

            Mocker.GetMock<IUpgradeMediaFiles>().Verify(v => v.UpgradeEpisodeFile(It.Is<EpisodeFile>(c => c.OriginalFilePath == $"{name}.mkv".AsOsAgnostic()), It.IsAny<LocalEpisode>(), It.IsAny<bool>()));
        }

        [Test]
        public void should_get_relative_path_when_there_is_no_grandparent_for_UNC_path()
        {
            WindowsOnly();

            var name = "Series.Title.S01E01.720p.HDTV.x264-Sonarr";
            var outputPath = @"\\server\share";
            var localEpisode = _approvedDecisions.First().LocalEpisode;

            localEpisode.FolderEpisodeInfo = new ParsedEpisodeInfo { ReleaseTitle = name };
            localEpisode.Path = Path.Combine(outputPath, name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, null);

            Mocker.GetMock<IUpgradeMediaFiles>().Verify(v => v.UpgradeEpisodeFile(It.Is<EpisodeFile>(c => c.OriginalFilePath == $"{name}.mkv"), It.IsAny<LocalEpisode>(), It.IsAny<bool>()));
        }

        [Test]
        public void should_use_folder_info_release_title_to_find_relative_path_when_file_is_not_in_download_client_item_output_directory()
        {
            var name = "Series.Title.S01E01.720p.HDTV.x264-Sonarr";
            var outputPath = Path.Combine(@"C:\Test\Unsorted\TV\".AsOsAgnostic(), name);
            var localEpisode = _approvedDecisions.First().LocalEpisode;

            _downloadClientItem.OutputPath = new OsPath(Path.Combine(@"C:\Test\Unsorted\TV-Other\".AsOsAgnostic(), name));
            localEpisode.FolderEpisodeInfo = new ParsedEpisodeInfo { ReleaseTitle = name };
            localEpisode.Path = Path.Combine(outputPath, "subfolder", name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, _downloadClientItem);

            Mocker.GetMock<IUpgradeMediaFiles>().Verify(v => v.UpgradeEpisodeFile(It.Is<EpisodeFile>(c => c.OriginalFilePath == $"{name}\\subfolder\\{name}.mkv".AsOsAgnostic()), It.IsAny<LocalEpisode>(), It.IsAny<bool>()));
        }

        [Test]
        public void should_update_existing_metadata_and_links_atomically_at_same_path()
        {
            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.GetFilesWithRelativePath(It.IsAny<int>(), It.IsAny<string>()))
                  .Returns(Builder<EpisodeFile>.CreateListOfSize(1).BuildList());

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, false);

            Mocker.GetMock<IEpisodeTrackFileService>()
                .Verify(v => v.UpdateFile(It.Is<EpisodeFile>(f => f.Id == 1), It.IsAny<List<EpisodeTrackFile>>(), true), Times.Once());
            Mocker.GetMock<IMediaFileService>()
                .Verify(v => v.Delete(It.IsAny<EpisodeFile>(), It.IsAny<DeleteMediaFileReason>()), Times.Never());
        }

        [Test]
        public void should_use_folder_info_release_title_to_find_relative_path_when_download_client_item_has_an_empty_output_path()
        {
            var name = "Series.Title.S01E01.720p.HDTV.x264-Sonarr";
            var outputPath = Path.Combine(@"C:\Test\Unsorted\TV\".AsOsAgnostic(), name);
            var localEpisode = _approvedDecisions.First().LocalEpisode;

            _downloadClientItem.OutputPath = default(OsPath);
            localEpisode.FolderEpisodeInfo = new ParsedEpisodeInfo { ReleaseTitle = name };
            localEpisode.Path = Path.Combine(outputPath, "subfolder", name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, _downloadClientItem);

            Mocker.GetMock<IUpgradeMediaFiles>().Verify(v => v.UpgradeEpisodeFile(It.Is<EpisodeFile>(c => c.OriginalFilePath == $"{name}\\subfolder\\{name}.mkv".AsOsAgnostic()), It.IsAny<LocalEpisode>(), It.IsAny<bool>()));
        }

        [Test]
        public void should_include_scene_name_with_new_downloads()
        {
            var firstDecision = _approvedDecisions.First();
            firstDecision.LocalEpisode.SceneName = "Series.Title.S01E01.dvdrip-DRONE";

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true);

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Verify(v => v.UpgradeEpisodeFile(It.Is<EpisodeFile>(e => e.SceneName == firstDecision.LocalEpisode.SceneName), _approvedDecisions.First().LocalEpisode, false),
                      Times.Once());
        }

        [Test]
        public void explicit_empty_targets_should_skip_without_writing_file_or_ownership()
        {
            var decision = _approvedDecisions.First();
            decision.LocalEpisode.TargetQualityTrackIds = new List<int>();

            Subject.Import(new List<ImportDecision> { decision }, true).Should().ContainSingle().Which.Result.Should().Be(ImportResultType.Skipped);

            Mocker.GetMock<IUpgradeMediaFiles>().Verify(s => s.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), It.IsAny<LocalEpisode>(), It.IsAny<bool>()), Times.Never());
            Mocker.GetMock<IEpisodeTrackFileService>().Verify(s => s.ImportFile(It.IsAny<EpisodeFile>(), It.IsAny<List<EpisodeTrackFile>>()), Times.Never());
        }

        [Test]
        public void duplicate_existing_path_records_should_require_ownership_resolution()
        {
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFilesWithRelativePath(It.IsAny<int>(), It.IsAny<string>()))
                .Returns(new List<EpisodeFile> { new EpisodeFile { Id = 1 }, new EpisodeFile { Id = 2 } });

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, false).Should().ContainSingle().Which.Result.Should().Be(ImportResultType.Skipped);

            Mocker.GetMock<IEpisodeTrackFileService>().Verify(s => s.UpdateFile(It.IsAny<EpisodeFile>(), It.IsAny<List<EpisodeTrackFile>>(), It.IsAny<bool>()), Times.Never());
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void script_import_should_pass_committed_file_identity_to_extra_file_matching()
        {
            var local = _approvedDecisions.First().LocalEpisode;
            local.ScriptImported = true;
            local.FileNameBeforeRename = "original.mkv";
            local.PossibleExtraFiles = new List<string> { "original.en.srt" };
            Mocker.GetMock<IUpgradeMediaFiles>()
                .Setup(s => s.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), local, It.IsAny<bool>()))
                .Returns<EpisodeFile, LocalEpisode, bool>((file, episode, copy) =>
                {
                    file.Id = 42;
                    file.RelativePath = "script-renamed.mkv";
                    return new EpisodeFileMoveResult { EpisodeFile = file };
                });

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true).Should().ContainSingle().Which.Result.Should().Be(ImportResultType.Imported);

            Mocker.GetMock<IExistingExtraFiles>().Verify(s => s.ImportExtraFiles(local.Series, local.PossibleExtraFiles, "original.mkv", 42), Times.Once());
            Mocker.GetMock<IExtraService>().Verify(s => s.MoveFilesAfterRename(local.Series, It.Is<EpisodeFile>(f => f.Id == 42), false), Times.Once());
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        public void published_import_should_report_logical_and_physical_upgrade_status(bool logicalUpgrade, bool deletedFile)
        {
            var local = _approvedDecisions.First().LocalEpisode;
            local.IsUpgrade = logicalUpgrade;
            var previousFiles = deletedFile ? new List<DeletedEpisodeFile> { new DeletedEpisodeFile(new EpisodeFile { Id = 99 }, null) } : new List<DeletedEpisodeFile>();
            Mocker.GetMock<IUpgradeMediaFiles>().Setup(s => s.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), local, It.IsAny<bool>()))
                .Returns(new EpisodeFileMoveResult { OldFiles = previousFiles });
            EpisodeImportedEvent imported = null;
            Mocker.GetMock<IEventAggregator>().Setup(s => s.PublishEvent(It.IsAny<EpisodeImportedEvent>())).Callback<EpisodeImportedEvent>(message => imported = message);

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true);

            imported.Should().NotBeNull();
            imported.IsUpgrade.Should().Be(logicalUpgrade || deletedFile);
        }

        [Test]
        public void committed_import_should_publish_history_event_even_if_extra_processing_fails()
        {
            Mocker.GetMock<IExtraService>().Setup(s => s.ImportEpisode(It.IsAny<LocalEpisode>(), It.IsAny<EpisodeFile>(), It.IsAny<bool>()))
                .Throws(new IOException("Extra file store unavailable"));

            var results = Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true);

            results.Should().ContainSingle().Which.Result.Should().Be(ImportResultType.Imported);
            Mocker.GetMock<IEventAggregator>().Verify(s => s.PublishEvent(It.IsAny<EpisodeImportedEvent>()), Times.Once());
            ExceptionVerification.ExpectedWarns(1);
        }
    }
}
