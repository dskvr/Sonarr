using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Extras;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles
{
    public class UpgradeMediaFileServiceFixture : CoreTest<UpgradeMediaFileService>
    {
        private EpisodeFile _episodeFile;
        private EpisodeFile _oldFile;
        private LocalEpisode _localEpisode;
        private List<EpisodeFile> _files;
        private List<EpisodeTrackFile> _links;
        private List<SeriesQualityTrack> _tracks;
        private string _destination;
        private string _oldPath;
        private bool _failCommit;

        [SetUp]
        public void Setup()
        {
            var appData = Path.Combine(TempFolder, "appdata");
            Directory.CreateDirectory(appData);
            Mocker.GetMock<IAppFolderInfo>().SetupGet(s => s.AppDataFolder).Returns(appData);
            var series = new Series { Id = 1, Path = Path.Combine(TempFolder, "series") };
            Mocker.GetMock<ISeriesService>().Setup(s => s.GetSeries(1)).Returns(series);
            Directory.CreateDirectory(series.Path);
            var source = Path.Combine(TempFolder, "download.mkv");
            File.WriteAllText(source, "new episode bytes");
            _destination = Path.Combine(series.Path, "new.mkv");
            _oldPath = Path.Combine(series.Path, "old.mkv");
            File.WriteAllText(_oldPath, "old episode bytes");
            _oldFile = new EpisodeFile { Id = 1, SeriesId = 1, RelativePath = "old.mkv", Series = series };
            _episodeFile = new EpisodeFile { SeriesId = 1, Series = series, Path = source, Size = new FileInfo(source).Length, DateAdded = DateTime.UtcNow };
            _localEpisode = new LocalEpisode
            {
                Series = series,
                Path = source,
                Episodes = [new Episode { Id = 10, SeriesId = 1, EpisodeFileId = 1, EpisodeFile = _oldFile }],
                TargetQualityTrackIds = [1]
            };
            _files = [_oldFile];
            _links = [new EpisodeTrackFile { EpisodeId = 10, TrackId = 1, EpisodeFileId = 1 }];
            _tracks =
            [
                new SeriesQualityTrack { Id = 1, SeriesId = 1, IsPrimary = true, Enabled = true },
                new SeriesQualityTrack { Id = 2, SeriesId = 1, Enabled = true }
            ];
            _failCommit = false;

            Mocker.GetMock<IDiskProvider>().Setup(p => p.FolderExists(It.IsAny<string>())).Returns<string>(Directory.Exists);
            Mocker.GetMock<IDiskProvider>().Setup(p => p.FileExists(It.IsAny<string>())).Returns<string>(File.Exists);
            Mocker.GetMock<IDiskProvider>().Setup(p => p.GetFileSize(It.IsAny<string>())).Returns<string>(p => new FileInfo(p).Length);
            Mocker.GetMock<IDiskProvider>().Setup(p => p.GetParentFolder(It.IsAny<string>())).Returns<string>(Path.GetDirectoryName);
            Mocker.GetMock<IDiskProvider>().Setup(p => p.CreateFolder(It.IsAny<string>())).Callback<string>(p => Directory.CreateDirectory(p));
            Mocker.GetMock<IDiskProvider>().Setup(p => p.MoveFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>())).Callback<string, string, bool>(File.Move);
            Mocker.GetMock<IDiskProvider>().Setup(p => p.DeleteFile(It.IsAny<string>())).Callback<string>(File.Delete);
            Mocker.GetMock<IDiskProvider>().Setup(p => p.DeleteFolder(It.IsAny<string>(), It.IsAny<bool>())).Callback<string, bool>(Directory.Delete);
            Mocker.GetMock<IDiskProvider>().Setup(p => p.FolderEmpty(It.IsAny<string>())).Returns<string>(path => Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any());
            Mocker.GetMock<IDiskProvider>().Setup(p => p.GetDirectories(It.IsAny<string>())).Returns<string>(Directory.GetDirectories);
            Mocker.GetMock<IDiskProvider>().Setup(p => p.WriteAllText(It.IsAny<string>(), It.IsAny<string>())).Callback<string, string>(File.WriteAllText);
            Mocker.GetMock<IDiskProvider>().Setup(p => p.ReadAllText(It.IsAny<string>())).Returns<string>(File.ReadAllText);
            Mocker.GetMock<IDiskTransferService>()
                .Setup(p => p.TransferFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TransferMode>(), false))
                .Returns<string, string, TransferMode, bool>((sourcePath, target, mode, overwrite) =>
                {
                    File.Copy(sourcePath, target, overwrite);
                    return mode;
                });
            Mocker.GetMock<IBuildFileNames>()
                .Setup(p => p.BuildFilePath(It.IsAny<List<Episode>>(), series, _episodeFile, It.IsAny<string>(), null, _localEpisode.CustomFormats))
                .Returns(() => _destination);
            Mocker.GetMock<IMoveEpisodeFiles>()
                .Setup(p => p.MoveEpisodeFile(_episodeFile, _localEpisode))
                .Returns(() =>
                {
                    File.Move(_episodeFile.Path, _destination);
                    _episodeFile.RelativePath = Path.GetRelativePath(_localEpisode.Series.Path, _destination);
                    return _episodeFile;
                });
            Mocker.GetMock<IMoveEpisodeFiles>()
                .Setup(p => p.CopyEpisodeFile(_episodeFile, _localEpisode))
                .Returns(() =>
                {
                    File.Copy(_episodeFile.Path, _destination);
                    _episodeFile.RelativePath = Path.GetRelativePath(_localEpisode.Series.Path, _destination);
                    return _episodeFile;
                });
            Mocker.GetMock<ISeriesQualityTrackService>().Setup(p => p.GetEnabledTracks(1)).Returns(() => _tracks.Where(t => t.Enabled).ToList());
            Mocker.GetMock<IEpisodeTrackFileService>().Setup(p => p.GetForSeries(1)).Returns(() => _links.ToList());
            Mocker.GetMock<IEpisodeTrackFileService>().Setup(p => p.IsFileReferenced(It.IsAny<int>())).Returns<int>(id => _links.Any(l => l.EpisodeFileId == id));
            Mocker.GetMock<IEpisodeTrackFileService>()
                .Setup(p => p.ImportFile(_episodeFile, It.IsAny<List<EpisodeTrackFile>>()))
                .Returns<EpisodeFile, List<EpisodeTrackFile>>((file, links) =>
                {
                    if (_failCommit)
                    {
                        throw new IOException("Database commit failed");
                    }

                    file.Id = 100;
                    foreach (var link in links)
                    {
                        _links.RemoveAll(l => l.EpisodeId == link.EpisodeId && l.TrackId == link.TrackId);
                        link.EpisodeFileId = file.Id;
                        _links.Add(link);
                    }

                    _files.Add(file);
                    return [1];
                });
            Mocker.GetMock<IMediaFileService>().Setup(p => p.GetFilesBySeries(1)).Returns(() => _files.ToList());
            Mocker.GetMock<IMediaFileService>().Setup(p => p.Get(It.IsAny<IEnumerable<int>>())).Returns<IEnumerable<int>>(ids => _files.Where(f => ids.Contains(f.Id)).ToList());
            Mocker.GetMock<IMediaFileService>().Setup(p => p.GetFilesWithRelativePath(1, It.IsAny<string>())).Returns<int, string>((id, path) => _files.Where(f => f.RelativePath == path).ToList());
            Mocker.GetMock<IMediaFileService>().Setup(p => p.Delete(It.IsAny<EpisodeFile>(), DeleteMediaFileReason.Upgrade)).Callback<EpisodeFile, DeleteMediaFileReason>((file, reason) => _files.RemoveAll(f => f.Id == file.Id));
            Mocker.GetMock<IRecycleBinProvider>().Setup(p => p.DeleteFile(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>((path, subfolder) =>
            {
                var recycled = Path.Combine(TempFolder, "recycled-" + Guid.NewGuid().ToString("N"));
                File.Move(path, recycled);
                return recycled;
            });
        }

        [Test]
        public void should_preserve_existing_file_when_transfer_fails()
        {
            Mocker.GetMock<IMoveEpisodeFiles>().Setup(p => p.MoveEpisodeFile(_episodeFile, _localEpisode)).Throws(new IOException("Transfer failed"));
            Assert.Throws<IOException>(() => Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode));
            AssertOldFileUnchanged();
        }

        [Test]
        public void should_preserve_existing_file_when_staging_fails()
        {
            _destination = _oldPath;
            Mocker.GetMock<IDiskTransferService>().Setup(p => p.TransferFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TransferMode>(), false)).Throws(new IOException("Copy failed"));
            Assert.Throws<IOException>(() => Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode));
            AssertOldFileUnchanged();
        }

        [Test]
        public void should_preserve_existing_file_when_database_commit_fails()
        {
            _failCommit = true;
            Assert.Throws<IOException>(() => Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode));
            AssertOldFileUnchanged();
            File.Exists(_destination).Should().BeFalse();
        }

        [Test]
        public void should_recycle_old_file_after_last_link_is_replaced()
        {
            var result = Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode);
            result.EpisodeFile.Id.Should().Be(100);
            result.OldFiles.Should().ContainSingle();
            File.ReadAllText(result.OldFiles[0].RecycleBinPath).Should().Be("old episode bytes");
            File.ReadAllText(_destination).Should().Be("new episode bytes");
            File.Exists(_localEpisode.Path).Should().BeFalse();
            _links.Single().EpisodeFileId.Should().Be(100);
        }

        [Test]
        public void should_preserve_file_shared_with_another_track()
        {
            _links.Add(new EpisodeTrackFile { EpisodeId = 10, TrackId = 2, EpisodeFileId = 1 });
            Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode).OldFiles.Should().BeEmpty();
            File.ReadAllText(_oldPath).Should().Be("old episode bytes");
            _links.Single(l => l.TrackId == 2).EpisodeFileId.Should().Be(1);
            _links.Single(l => l.TrackId == 1).EpisodeFileId.Should().Be(100);
        }

        [Test]
        public void should_preserve_file_shared_with_another_episode()
        {
            _links.Add(new EpisodeTrackFile { EpisodeId = 11, TrackId = 1, EpisodeFileId = 1 });
            Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode).OldFiles.Should().BeEmpty();
            File.ReadAllText(_oldPath).Should().Be("old episode bytes");
            _links.Single(l => l.EpisodeId == 11).EpisodeFileId.Should().Be(1);
        }

        [Test]
        public void should_preserve_file_referenced_by_disabled_track()
        {
            _tracks[1].Enabled = false;
            _links.Add(new EpisodeTrackFile { EpisodeId = 10, TrackId = 2, EpisodeFileId = 1 });
            Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode).OldFiles.Should().BeEmpty();
            File.ReadAllText(_oldPath).Should().Be("old episode bytes");
        }

        [Test]
        public void should_link_one_file_to_overlapping_targets()
        {
            _localEpisode.TargetQualityTrackIds = [1, 2];
            Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode);
            _links.Should().HaveCount(2).And.OnlyContain(l => l.EpisodeFileId == 100);
            _files.Should().ContainSingle();
        }

        [Test]
        public void should_upgrade_same_path_when_old_file_has_no_surviving_links()
        {
            _destination = _oldPath;
            var result = Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode);
            File.ReadAllText(_destination).Should().Be("new episode bytes");
            File.ReadAllText(result.OldFiles.Single().RecycleBinPath).Should().Be("old episode bytes");
        }

        [Test]
        public void should_restore_same_path_after_commit_failure()
        {
            _destination = _oldPath;
            _failCommit = true;
            Assert.Throws<IOException>(() => Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode));
            AssertOldFileUnchanged();
        }

        [Test]
        public void should_restore_same_path_after_transfer_failure()
        {
            _destination = _oldPath;
            Mocker.GetMock<IMoveEpisodeFiles>().Setup(p => p.MoveEpisodeFile(_episodeFile, _localEpisode)).Throws(new IOException("Transfer failed"));
            Assert.Throws<IOException>(() => Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode));
            AssertOldFileUnchanged();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_block_collision_with_surviving_file(bool caseOnly)
        {
            _destination = caseOnly ? Path.Combine(_localEpisode.Series.Path, "OLD.mkv") : _oldPath;
            _links.Add(new EpisodeTrackFile { EpisodeId = 10, TrackId = 2, EpisodeFileId = 1 });
            Assert.Throws<DestinationAlreadyExistsException>(() => Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode));
            AssertOldFileUnchanged();
        }

        [Test]
        public void should_not_overwrite_untracked_destination()
        {
            File.WriteAllText(_destination, "untracked bytes");
            Assert.Throws<DestinationAlreadyExistsException>(() => Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode));
            File.ReadAllText(_destination).Should().Be("untracked bytes");
            AssertOldFileUnchanged();
        }

        [Test]
        public void should_keep_source_for_copy_import()
        {
            Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode, true);
            File.ReadAllText(_localEpisode.Path).Should().Be("new episode bytes");
        }

        [Test]
        public void should_reject_disabled_persisted_target()
        {
            _localEpisode.TargetQualityTrackIds = [2];
            _tracks[1].Enabled = false;
            Assert.Throws<InvalidOperationException>(() => Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode));
            AssertOldFileUnchanged();
        }

        [Test]
        public void should_reject_empty_target_instead_of_falling_back()
        {
            _localEpisode.TargetQualityTrackIds = [];
            Assert.Throws<InvalidOperationException>(() => Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode));
            AssertOldFileUnchanged();
        }

        [Test]
        public void should_use_primary_for_legacy_download()
        {
            _localEpisode.TargetQualityTrackIds = null;
            Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode);
            _links.Single().TrackId.Should().Be(1);
        }

        [Test]
        public void should_throw_if_root_folder_is_missing()
        {
            Mocker.GetMock<IDiskProvider>().Setup(p => p.FolderExists(TempFolder)).Returns(false);
            Assert.Throws<RootFolderNotFoundException>(() => Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode));
            AssertOldFileUnchanged();
        }

        [Test]
        public void should_restore_backup_after_restart_before_commit()
        {
            var folder = Path.Combine(_localEpisode.Series.Path, ".sonarr-import", "interrupted");
            Directory.CreateDirectory(folder);
            var backup = Path.Combine(folder, "old.mkv");
            File.Move(_oldPath, backup);
            File.WriteAllText(_oldPath, "uncommitted bytes");
            var journal = new UpgradeMediaFileService.ImportJournal
            {
                SeriesId = 1,
                SourcePath = _localEpisode.Path,
                DestinationPath = _oldPath,
                StagedPath = Path.Combine(folder, "incoming.mkv"),
                BackupPath = backup,
                BackupReady = true,
                BackupSize = new FileInfo(backup).Length,
                OldFileIds = [1],
                EpisodeIds = [10],
                TrackIds = [1],
                DateAdded = _episodeFile.DateAdded
            };
            WriteImportReceipt(folder, journal);
            Subject.RecoverImports(_localEpisode.Series);
            AssertOldFileUnchanged();
            Directory.Exists(folder).Should().BeFalse();
        }

        [Test]
        public void should_finish_cleanup_after_restart_following_commit()
        {
            Mocker.GetMock<IRecycleBinProvider>()
                .SetupSequence(s => s.DeleteFile(It.IsAny<string>(), It.IsAny<string>()))
                .Throws(new IOException("Recycle destination unavailable"))
                .Returns("recovered");

            var result = Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode);
            result.EpisodeFile.Id.Should().Be(100);
            _links.Single().EpisodeFileId.Should().Be(100);

            Subject.RecoverImports(_localEpisode.Series);

            File.ReadAllText(_destination).Should().Be("new episode bytes");
            _files.Should().ContainSingle(f => f.Id == 100);
            _links.Single().EpisodeFileId.Should().Be(100);
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_preserve_new_file_when_same_completion_is_processed_concurrently()
        {
            var succeeded = 0;
            var subject = Subject;
            Parallel.For(0, 2, _ =>
            {
                try
                {
                    subject.UpgradeEpisodeFile(_episodeFile, _localEpisode);
                    System.Threading.Interlocked.Increment(ref succeeded);
                }
                catch (FileNotFoundException)
                {
                    // The first completion already consumed the original download.
                }
            });

            succeeded.Should().Be(1);
            File.ReadAllText(_destination).Should().Be("new episode bytes");
            _links.Should().ContainSingle(l => l.EpisodeFileId == 100);
        }

        [Test]
        public void should_snapshot_all_old_ownership_for_deletion_events()
        {
            _localEpisode.Episodes.Add(new Episode { Id = 11, SeriesId = 1 });
            _links.Add(new EpisodeTrackFile { EpisodeId = 11, TrackId = 1, EpisodeFileId = 1 });
            Mocker.GetMock<IEpisodeService>().Setup(s => s.GetEpisodes(It.IsAny<IEnumerable<int>>())).Returns(_localEpisode.Episodes);

            var result = Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode);

            result.OldFiles.Single().EpisodeFile.Episodes.Value.Should().HaveCount(2);
            result.OldFiles.Single().EpisodeFile.TrackFiles.Value.Should().HaveCount(2);
            _localEpisode.IsUpgrade.Should().BeTrue();
        }

        private string WriteImportReceipt(string workDirectory, UpgradeMediaFileService.ImportJournal journal)
        {
            var operationDirectory = Path.Combine(TempFolder, "appdata", "MediaFileRecovery", "files", "1", Path.GetFileName(workDirectory));
            Directory.CreateDirectory(operationDirectory);
            journal.WorkDirectory = workDirectory;
            journal.ResolvedWorkDirectory = Path.GetDirectoryName(MediaFileRecoveryPaths.ResolveFilePath(Path.Combine(workDirectory, ".receipt")));
            if (journal.BackupPath != null)
            {
                journal.ResolvedBackupPath ??= MediaFileRecoveryPaths.ResolveFilePath(journal.BackupPath);
            }

            journal.ResolvedDestinationPath ??= MediaFileRecoveryPaths.ResolveFilePath(journal.DestinationPath);
            journal.ResolvedStagedPath ??= MediaFileRecoveryPaths.ResolveFilePath(journal.StagedPath);
            if (journal.BackupOriginalPath != null)
            {
                journal.ResolvedBackupOriginalPath ??= MediaFileRecoveryPaths.ResolveFilePath(journal.BackupOriginalPath);
            }

            File.WriteAllText(Path.Combine(operationDirectory, "import.json"), journal.ToJson());
            return operationDirectory;
        }

        private void AssertOldFileUnchanged()
        {
            File.ReadAllText(_oldPath).Should().Be("old episode bytes");
            File.ReadAllText(_localEpisode.Path).Should().Be("new episode bytes");
            _links.Single(l => l.EpisodeId == 10 && l.TrackId == 1).EpisodeFileId.Should().Be(1);
            Mocker.GetMock<IRecycleBinProvider>().Verify(p => p.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
            Mocker.GetMock<IMediaFileService>().Verify(p => p.Delete(It.IsAny<EpisodeFile>(), It.IsAny<DeleteMediaFileReason>()), Times.Never());
        }

        [Test]
        public void should_reject_stale_lower_quality_decision_after_higher_quality_import()
        {
            var lowerPath = Path.Combine(TempFolder, "720p.mkv");
            File.WriteAllText(lowerPath, "lower quality bytes");
            var lower = _localEpisode.Clone();
            lower.Path = lowerPath;
            lower.Quality = new QualityModel(Quality.HDTV720p);
            lower.ExpectedTrackFiles = [new EpisodeTrackFile { EpisodeId = 10, TrackId = 1, EpisodeFileId = 1 }];
            _localEpisode.Quality = new QualityModel(Quality.HDTV1080p);

            Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode);
            var lowerFile = new EpisodeFile { SeriesId = 1, Path = lowerPath, Size = new FileInfo(lowerPath).Length };
            Assert.Throws<InvalidOperationException>(() => Subject.UpgradeEpisodeFile(lowerFile, lower));

            File.ReadAllText(_destination).Should().Be("new episode bytes");
            File.ReadAllText(lowerPath).Should().Be("lower quality bytes");
            _links.Single().EpisodeFileId.Should().Be(100);
        }

        [Test]
        public void should_preserve_backup_if_file_regained_references_after_commit()
        {
            _destination = _oldPath;
            Mocker.GetMock<IRecycleBinProvider>().Setup(s => s.DeleteFile(It.IsAny<string>(), It.IsAny<string>())).Throws(new IOException("Recycle failed"));
            Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode);
            _links.Add(new EpisodeTrackFile { EpisodeId = 11, TrackId = 2, EpisodeFileId = 1 });

            Assert.Throws<IOException>(() => Subject.RecoverImports(_localEpisode.Series));

            var backup = Directory.GetFiles(Path.Combine(_localEpisode.Series.Path, ".sonarr-import"), "old.mkv", SearchOption.AllDirectories).Single();
            File.ReadAllText(backup).Should().Be("old episode bytes");
            File.ReadAllText(_destination).Should().Be("new episode bytes");
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_recycle_from_receipt_after_old_database_row_was_cleaned()
        {
            _destination = _oldPath;
            Mocker.GetMock<IRecycleBinProvider>().Setup(s => s.DeleteFile(It.IsAny<string>(), It.IsAny<string>())).Throws(new IOException("Recycle failed"));
            Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode);
            _files.RemoveAll(f => f.Id == 1);
            var recycled = Path.Combine(TempFolder, "recovered-old.mkv");
            Mocker.GetMock<IRecycleBinProvider>().Setup(s => s.DeleteFile(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>((path, subfolder) =>
            {
                File.Move(path, recycled);
                return recycled;
            });

            Subject.RecoverImports(_localEpisode.Series);

            File.ReadAllText(recycled).Should().Be("old episode bytes");
            File.ReadAllText(_destination).Should().Be("new episode bytes");
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_reject_recovery_destination_with_parent_traversal()
        {
            var folder = Path.Combine(_localEpisode.Series.Path, ".sonarr-import", "invalid");
            Directory.CreateDirectory(folder);
            var victim = Path.Combine(TempFolder, "outside.mkv");
            File.WriteAllText(victim, "outside bytes");
            var journal = new UpgradeMediaFileService.ImportJournal
            {
                SeriesId = 1,
                DestinationPath = Path.Combine(_localEpisode.Series.Path, "..", "outside.mkv"),
                StagedPath = Path.Combine(folder, "incoming.mkv"),
                OldFileIds = [],
                EpisodeIds = [10],
                TrackIds = [1]
            };
            WriteImportReceipt(folder, journal);

            Assert.Throws<IOException>(() => Subject.RecoverImports(_localEpisode.Series));

            File.ReadAllText(victim).Should().Be("outside bytes");
            File.ReadAllText(_oldPath).Should().Be("old episode bytes");
        }

        [Test]
        public void should_reject_recovery_destination_after_symlink_target_changes()
        {
            PosixOnly();
            var folder = Path.Combine(_localEpisode.Series.Path, ".sonarr-import", "invalid");
            Directory.CreateDirectory(folder);
            var outside = Path.Combine(TempFolder, "outside");
            Directory.CreateDirectory(outside);
            var victim = Path.Combine(outside, "victim.mkv");
            File.WriteAllText(victim, "outside bytes");
            var link = Path.Combine(_localEpisode.Series.Path, "linked");
            Directory.CreateSymbolicLink(link, outside);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.GetFileAttributes(It.IsAny<string>())).Returns<string>(File.GetAttributes);
            var journal = new UpgradeMediaFileService.ImportJournal
            {
                SeriesId = 1,
                DestinationPath = Path.Combine(link, "victim.mkv"),
                StagedPath = Path.Combine(folder, "incoming.mkv"),
                OldFileIds = [],
                EpisodeIds = [10],
                TrackIds = [1]
            };
            WriteImportReceipt(folder, journal);
            var replacement = Path.Combine(TempFolder, "replacement");
            Directory.CreateDirectory(replacement);
            Directory.Delete(link);
            Directory.CreateSymbolicLink(link, replacement);

            Assert.Throws<IOException>(() => Subject.RecoverImports(_localEpisode.Series));

            File.ReadAllText(victim).Should().Be("outside bytes");
        }

        [Test]
        public void should_recover_rename_interrupted_before_moving_bytes()
        {
            var receipt = Subject.BeginRename(_localEpisode.Series, _oldFile, _destination);

            Subject.RecoverImports(_localEpisode.Series);
            Subject.RecoverImports(_localEpisode.Series);

            File.ReadAllText(_oldPath).Should().Be("old episode bytes");
            File.Exists(_destination).Should().BeFalse();
            Directory.Exists(receipt).Should().BeFalse();
            _oldFile.RelativePath.Should().Be("old.mkv");
            _links.Single().EpisodeFileId.Should().Be(1);
        }

        [Test]
        public void should_restore_rename_interrupted_after_move_before_database_commit()
        {
            var receipt = Subject.BeginRename(_localEpisode.Series, _oldFile, _destination);
            File.Move(_oldPath, _destination);

            Subject.RecoverImports(_localEpisode.Series);
            Subject.RecoverImports(_localEpisode.Series);

            File.ReadAllText(_oldPath).Should().Be("old episode bytes");
            File.Exists(_destination).Should().BeFalse();
            Directory.Exists(receipt).Should().BeFalse();
            _oldFile.RelativePath.Should().Be("old.mkv");
            _links.Single().EpisodeFileId.Should().Be(1);
        }

        [Test]
        public void should_finish_rename_interrupted_after_database_commit()
        {
            var receipt = Subject.BeginRename(_localEpisode.Series, _oldFile, _destination);
            File.Move(_oldPath, _destination);
            _oldFile.RelativePath = "new.mkv";

            Subject.RecoverImports(_localEpisode.Series);
            Subject.RecoverImports(_localEpisode.Series);

            File.ReadAllText(_destination).Should().Be("old episode bytes");
            File.Exists(_oldPath).Should().BeFalse();
            Directory.Exists(receipt).Should().BeFalse();
            _oldFile.RelativePath.Should().Be("new.mkv");
            _links.Single().EpisodeFileId.Should().Be(1);
            Mocker.GetMock<IEventAggregator>().Verify(s => s.PublishEvent(It.IsAny<EpisodeFileRenamedEvent>()), Times.Once());
        }

        [Test]
        public void should_preserve_both_paths_if_rename_recovery_finds_conflicting_bytes()
        {
            var receipt = Subject.BeginRename(_localEpisode.Series, _oldFile, _destination);
            File.WriteAllText(_destination, "unrelated bytes");

            Assert.Throws<IOException>(() => Subject.RecoverImports(_localEpisode.Series));

            File.ReadAllText(_oldPath).Should().Be("old episode bytes");
            File.ReadAllText(_destination).Should().Be("unrelated bytes");
            Directory.Exists(receipt).Should().BeTrue();
            _links.Single().EpisodeFileId.Should().Be(1);
        }

        [Test]
        public void should_preserve_rename_bytes_when_database_file_was_removed()
        {
            var receipt = Subject.BeginRename(_localEpisode.Series, _oldFile, _destination);
            File.Move(_oldPath, _destination);
            _files.Clear();

            Assert.Throws<IOException>(() => Subject.RecoverImports(_localEpisode.Series));

            File.ReadAllText(_destination).Should().Be("old episode bytes");
            Directory.Exists(receipt).Should().BeTrue();
        }

        [Test]
        public void should_reject_foreign_file_before_creating_rename_receipt()
        {
            _oldFile.SeriesId = 2;

            Assert.Throws<InvalidOperationException>(() => Subject.BeginRename(_localEpisode.Series, _oldFile, _destination));

            File.ReadAllText(_oldPath).Should().Be("old episode bytes");
            File.Exists(_destination).Should().BeFalse();
        }

        [Test]
        public void should_restore_case_only_rename_interrupted_at_transfer_intermediate()
        {
            var destination = Path.Combine(_localEpisode.Series.Path, "OLD.mkv");
            var receipt = Subject.BeginRename(_localEpisode.Series, _oldFile, destination);
            File.Move(_oldPath, _oldPath + ".backup~");

            Subject.RecoverImports(_localEpisode.Series);

            File.ReadAllText(_oldPath).Should().Be("old episode bytes");
            File.Exists(_oldPath + ".backup~").Should().BeFalse();
            Directory.Exists(receipt).Should().BeFalse();
            _links.Single().EpisodeFileId.Should().Be(1);
        }

        [Test]
        public void should_not_overwrite_preexisting_case_rename_intermediate()
        {
            var destination = Path.Combine(_localEpisode.Series.Path, "OLD.mkv");
            File.WriteAllText(_oldPath + ".backup~", "unrelated bytes");

            Assert.Throws<DestinationAlreadyExistsException>(() => Subject.BeginRename(_localEpisode.Series, _oldFile, destination));

            File.ReadAllText(_oldPath).Should().Be("old episode bytes");
            File.ReadAllText(_oldPath + ".backup~").Should().Be("unrelated bytes");
        }

        [Test]
        public void should_leave_rename_receipt_for_retry_when_event_publication_fails()
        {
            var receipt = Subject.BeginRename(_localEpisode.Series, _oldFile, _destination);
            File.Move(_oldPath, _destination);
            _oldFile.RelativePath = "new.mkv";
            Mocker.GetMock<IEventAggregator>().Setup(s => s.PublishEvent(It.IsAny<EpisodeFileRenamedEvent>())).Throws(new IOException("Event publication unavailable"));

            Assert.Throws<IOException>(() => Subject.RecoverImports(_localEpisode.Series));

            Directory.Exists(receipt).Should().BeTrue();
            File.ReadAllText(_destination).Should().Be("old episode bytes");
            Mocker.GetMock<IEventAggregator>().Setup(s => s.PublishEvent(It.IsAny<EpisodeFileRenamedEvent>()));
            Subject.RecoverImports(_localEpisode.Series);
            Directory.Exists(receipt).Should().BeFalse();
            _links.Single().EpisodeFileId.Should().Be(1);
        }

        [Test]
        public void should_reject_missing_committed_rename_destination_without_unlinking_file()
        {
            var receipt = Subject.BeginRename(_localEpisode.Series, _oldFile, _destination);
            _oldFile.RelativePath = "new.mkv";

            Assert.Throws<IOException>(() => Subject.RecoverImports(_localEpisode.Series));

            File.ReadAllText(_oldPath).Should().Be("old episode bytes");
            Directory.Exists(receipt).Should().BeTrue();
            _links.Single().EpisodeFileId.Should().Be(1);
        }

        [Test]
        public void should_reject_rename_recovery_when_file_metadata_changed_again()
        {
            var receipt = Subject.BeginRename(_localEpisode.Series, _oldFile, _destination);
            _oldFile.RelativePath = "another.mkv";

            Assert.Throws<IOException>(() => Subject.RecoverImports(_localEpisode.Series));

            File.ReadAllText(_oldPath).Should().Be("old episode bytes");
            Directory.Exists(receipt).Should().BeTrue();
        }

        [Test]
        public void should_reject_rename_recovery_when_another_file_owns_destination()
        {
            var receipt = Subject.BeginRename(_localEpisode.Series, _oldFile, _destination);
            File.Move(_oldPath, _destination);
            _files.Add(new EpisodeFile { Id = 2, SeriesId = 1, RelativePath = "new.mkv" });

            Assert.Throws<IOException>(() => Subject.RecoverImports(_localEpisode.Series));

            File.ReadAllText(_destination).Should().Be("old episode bytes");
            Directory.Exists(receipt).Should().BeTrue();
        }

        [Test]
        public void should_skip_rename_receipt_when_destination_is_unchanged()
        {
            Subject.BeginRename(_localEpisode.Series, _oldFile, _oldPath).Should().BeNull();
            Subject.FinishRename(_localEpisode.Series, null);

            File.ReadAllText(_oldPath).Should().Be("old episode bytes");
            Mocker.GetMock<IDiskProvider>().Verify(s => s.DeleteFolder(It.IsAny<string>(), It.IsAny<bool>()), Times.Never());
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void should_preserve_original_basename_with_real_mover_when_renaming_is_disabled(bool seasonFolder, bool copyOnly)
        {
            const string originalName = "Single.Profile.S01E01.2160p.mkv";
            var source = Path.Combine(TempFolder, originalName);
            File.Move(_localEpisode.Path, source);
            _localEpisode.Path = source;
            _episodeFile.Path = source;
            _localEpisode.Series.Title = "Single Profile";
            _localEpisode.Series.SeasonFolder = seasonFolder;
            _localEpisode.Episodes[0].SeasonNumber = 1;
            _localEpisode.Episodes[0].EpisodeNumber = 1;
            Mocker.GetMock<INamingConfigService>().Setup(s => s.GetConfig()).Returns(NamingConfig.Default);
            Mocker.GetMock<IRootFolderService>().Setup(s => s.GetBestRootFolderPath(_localEpisode.Series.Path)).Returns(TempFolder);
            Mocker.GetMock<IImportScript>().Setup(s => s.TryImport(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<LocalEpisode>(), It.IsAny<EpisodeFile>(), It.IsAny<TransferMode>())).Returns(ScriptImportDecision.DeferMove);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.CopyFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>())).Callback<string, string, bool>(File.Copy);
            Mocker.SetConstant<IBuildFileNames>(Mocker.Resolve<FileNameBuilder>());
            Mocker.SetConstant<IDiskTransferService>(Mocker.Resolve<DiskTransferService>());
            Mocker.SetConstant<IMoveEpisodeFiles>(Mocker.Resolve<EpisodeFileMovingService>());

            var result = Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode, copyOnly);

            var expectedRelativePath = seasonFolder ? Path.Combine("Season 1", originalName) : originalName;
            result.EpisodeFile.RelativePath.Should().Be(expectedRelativePath);
            File.ReadAllText(Path.Combine(_localEpisode.Series.Path, expectedRelativePath)).Should().Be("new episode bytes");
            File.Exists(source).Should().Be(copyOnly);
            Directory.GetFiles(_localEpisode.Series.Path, "incoming.mkv", SearchOption.AllDirectories).Should().BeEmpty();
        }

        [Test]
        public void should_import_through_stable_season_directory_symlink()
        {
            PosixOnly();
            var outside = Path.Combine(TempFolder, "season-storage");
            Directory.CreateDirectory(outside);
            var linked = Path.Combine(_localEpisode.Series.Path, "Season 1");
            Directory.CreateSymbolicLink(linked, outside);
            _destination = Path.Combine(linked, "new.mkv");
            Mocker.GetMock<IDiskProvider>().Setup(s => s.GetFileAttributes(It.IsAny<string>())).Returns<string>(File.GetAttributes);

            Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode);

            File.ReadAllText(Path.Combine(outside, "new.mkv")).Should().Be("new episode bytes");
            _links.Single().EpisodeFileId.Should().Be(100);
        }

        [Test]
        public void should_recover_rename_through_stable_season_directory_symlink()
        {
            PosixOnly();
            var outside = Path.Combine(TempFolder, "season-storage");
            Directory.CreateDirectory(outside);
            var linked = Path.Combine(_localEpisode.Series.Path, "Season 1");
            Directory.CreateSymbolicLink(linked, outside);
            File.Move(_oldPath, Path.Combine(outside, "old.mkv"));
            _oldPath = Path.Combine(linked, "old.mkv");
            _oldFile.RelativePath = Path.Combine("Season 1", "old.mkv");
            _destination = Path.Combine(linked, "new.mkv");
            Mocker.GetMock<IDiskProvider>().Setup(s => s.GetFileAttributes(It.IsAny<string>())).Returns<string>(File.GetAttributes);
            var receipt = Subject.BeginRename(_localEpisode.Series, _oldFile, _destination);
            File.Move(_oldPath, _destination);

            Subject.RecoverImports(_localEpisode.Series);

            File.ReadAllText(Path.Combine(outside, "old.mkv")).Should().Be("old episode bytes");
            File.Exists(Path.Combine(outside, "new.mkv")).Should().BeFalse();
            Directory.Exists(receipt).Should().BeFalse();
            _links.Single().EpisodeFileId.Should().Be(1);
        }

        [Test]
        public void should_ignore_forged_receipt_in_library_directory()
        {
            var workDirectory = Path.Combine(_localEpisode.Series.Path, ".sonarr-import", "forged");
            Directory.CreateDirectory(workDirectory);
            File.WriteAllText(Path.Combine(workDirectory, "import.json"), "{\"destinationPath\":\"../../outside\"}");

            Subject.RecoverImports(_localEpisode.Series);

            File.ReadAllText(_oldPath).Should().Be("old episode bytes");
            Directory.Exists(workDirectory).Should().BeTrue();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_preserve_original_script_source_and_accept_custom_final_name_with_real_mover(bool copyOnly)
        {
            var originalSource = _localEpisode.Path;
            var customDestination = Path.Combine(_localEpisode.Series.Path, "script-selected.mkv");
            Mocker.GetMock<INamingConfigService>().Setup(s => s.GetConfig()).Returns(NamingConfig.Default);
            Mocker.GetMock<IRootFolderService>().Setup(s => s.GetBestRootFolderPath(_localEpisode.Series.Path)).Returns(TempFolder);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.CopyFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>())).Callback<string, string, bool>(File.Copy);
            Mocker.GetMock<IImportScript>()
                .Setup(s => s.TryImport(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<LocalEpisode>(), It.IsAny<EpisodeFile>(), It.IsAny<TransferMode>()))
                .Returns<string, string, LocalEpisode, EpisodeFile, TransferMode>((source, destination, local, file, mode) =>
                {
                    source.Should().Be(originalSource);
                    destination.Should().Be(Path.Combine(_localEpisode.Series.Path, Path.GetFileName(originalSource)));
                    if (copyOnly)
                    {
                        mode.Should().Be(TransferMode.Copy);
                        File.Copy(source, customDestination);
                    }
                    else
                    {
                        mode.Should().Be(TransferMode.Move);
                        File.Move(source, customDestination);
                    }

                    file.Path = customDestination;
                    file.RelativePath = "script-selected.mkv";
                    local.ScriptImported = true;
                    return ScriptImportDecision.MoveComplete;
                });
            Mocker.SetConstant<IBuildFileNames>(Mocker.Resolve<FileNameBuilder>());
            Mocker.SetConstant<IDiskTransferService>(Mocker.Resolve<DiskTransferService>());
            Mocker.SetConstant<IMoveEpisodeFiles>(Mocker.Resolve<EpisodeFileMovingService>());

            var result = Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode, copyOnly);

            result.EpisodeFile.RelativePath.Should().Be("script-selected.mkv");
            result.EpisodeFile.Path.Should().Be(customDestination);
            File.ReadAllText(customDestination).Should().Be("new episode bytes");
            File.Exists(originalSource).Should().Be(copyOnly);
            _links.Single().EpisodeFileId.Should().Be(100);
        }

        [Test]
        public void should_preserve_original_bytes_if_backup_copy_leaves_partial_file()
        {
            _destination = _oldPath;
            Mocker.GetMock<IDiskTransferService>()
                .Setup(s => s.TransferFile(_oldPath, It.IsAny<string>(), TransferMode.Copy, false))
                .Returns<string, string, TransferMode, bool>((source, target, mode, overwrite) =>
                {
                    File.WriteAllText(target, "partial");
                    throw new IOException("Backup copy interrupted");
                });

            Assert.Throws<IOException>(() => Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode));

            AssertOldFileUnchanged();
        }

        [Test]
        public void should_reject_symlink_swap_after_staging_before_destination_write()
        {
            PosixOnly();
            var intended = Path.Combine(TempFolder, "intended-season");
            var replacement = Path.Combine(TempFolder, "replacement-season");
            Directory.CreateDirectory(intended);
            Directory.CreateDirectory(replacement);
            var linked = Path.Combine(_localEpisode.Series.Path, "Season 1");
            Directory.CreateSymbolicLink(linked, intended);
            _destination = Path.Combine(linked, "new.mkv");
            var victim = Path.Combine(replacement, "new.mkv");
            File.WriteAllText(victim, "unrelated bytes");
            Mocker.GetMock<IDiskTransferService>()
                .Setup(s => s.TransferFile(_localEpisode.Path, It.IsAny<string>(), It.IsAny<TransferMode>(), false))
                .Returns<string, string, TransferMode, bool>((source, target, mode, overwrite) =>
                {
                    File.Copy(source, target);
                    Directory.Delete(linked);
                    Directory.CreateSymbolicLink(linked, replacement);
                    return mode;
                });

            Assert.Throws<IOException>(() => Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode));

            AssertOldFileUnchanged();
            File.ReadAllText(victim).Should().Be("unrelated bytes");
            Mocker.GetMock<IEpisodeTrackFileService>().Verify(s => s.ImportFile(It.IsAny<EpisodeFile>(), It.IsAny<List<EpisodeTrackFile>>()), Times.Never());
        }

        [Test]
        public void should_preserve_recoverable_bytes_if_symlink_changes_during_transfer()
        {
            PosixOnly();
            var intended = Path.Combine(TempFolder, "intended-season");
            var replacement = Path.Combine(TempFolder, "replacement-season");
            Directory.CreateDirectory(intended);
            Directory.CreateDirectory(replacement);
            var linked = Path.Combine(_localEpisode.Series.Path, "Season 1");
            Directory.CreateSymbolicLink(linked, intended);
            _destination = Path.Combine(linked, "new.mkv");
            var victim = Path.Combine(replacement, "new.mkv");
            File.WriteAllText(victim, "unrelated bytes");
            Mocker.GetMock<IMoveEpisodeFiles>().Setup(s => s.MoveEpisodeFile(_episodeFile, _localEpisode)).Returns(() =>
            {
                File.Move(_episodeFile.Path, _destination);
                _episodeFile.RelativePath = Path.Combine("Season 1", "new.mkv");
                Directory.Delete(linked);
                Directory.CreateSymbolicLink(linked, replacement);
                return _episodeFile;
            });

            Assert.Throws<IOException>(() => Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode));

            AssertOldFileUnchanged();
            File.ReadAllText(victim).Should().Be("unrelated bytes");
            File.ReadAllText(Path.Combine(intended, "new.mkv")).Should().Be("new episode bytes");
            Directory.Delete(linked);
            Directory.CreateSymbolicLink(linked, intended);
            Subject.RecoverImports(_localEpisode.Series);
            File.Exists(Path.Combine(intended, "new.mkv")).Should().BeFalse();
            File.ReadAllText(_localEpisode.Path).Should().Be("new episode bytes");
        }

        [Test]
        public void should_preserve_unexpected_files_added_to_staging_directory()
        {
            string unexpectedPath = null;
            Mocker.GetMock<IDiskTransferService>()
                .Setup(s => s.TransferFile(_localEpisode.Path, It.IsAny<string>(), It.IsAny<TransferMode>(), false))
                .Returns<string, string, TransferMode, bool>((source, target, mode, overwrite) =>
                {
                    File.Copy(source, target);
                    unexpectedPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(target)), "unrelated.txt");
                    File.WriteAllText(unexpectedPath, "unrelated bytes");
                    return mode;
                });

            Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode);

            File.ReadAllText(unexpectedPath).Should().Be("unrelated bytes");
            File.ReadAllText(_destination).Should().Be("new episode bytes");
        }

        [Test]
        public void should_reject_same_base_filename_with_retained_different_container()
        {
            _destination = Path.ChangeExtension(_oldPath, ".mp4");
            _links.Add(new EpisodeTrackFile { EpisodeId = 10, TrackId = 2, EpisodeFileId = 1 });

            Assert.Throws<DestinationAlreadyExistsException>(() => Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode));

            AssertOldFileUnchanged();
            File.Exists(_destination).Should().BeFalse();
        }

        [Test]
        public void should_allow_container_change_when_old_file_is_exclusively_replaced()
        {
            _destination = Path.ChangeExtension(_oldPath, ".mp4");
            var newSource = Path.ChangeExtension(_localEpisode.Path, ".mp4");
            File.Move(_localEpisode.Path, newSource);
            _localEpisode.Path = newSource;
            _episodeFile.Path = newSource;

            Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode);

            File.ReadAllText(_destination).Should().Be("new episode bytes");
            _links.Single().EpisodeFileId.Should().Be(100);
        }

        [Test]
        public void should_retain_main_rename_receipt_until_strict_extras_complete()
        {
            var receipt = Subject.BeginRename(_localEpisode.Series, _oldFile, _destination);
            File.Move(_oldPath, _destination);
            _oldFile.RelativePath = "new.mkv";
            Mocker.GetMock<IExtraService>().Setup(s => s.MoveFilesAfterRename(_localEpisode.Series, _oldFile, true)).Throws(new IOException("Subtitle move failed"));

            Assert.Throws<IOException>(() => Subject.RecoverImports(_localEpisode.Series));

            File.ReadAllText(_destination).Should().Be("old episode bytes");
            Directory.Exists(receipt).Should().BeTrue();
            _links.Single().EpisodeFileId.Should().Be(1);
            Mocker.GetMock<IExtraService>().Setup(s => s.MoveFilesAfterRename(_localEpisode.Series, _oldFile, true));
            Subject.RecoverImports(_localEpisode.Series);
            Directory.Exists(receipt).Should().BeFalse();
        }

        [Test]
        public void should_not_escalate_nested_file_recovery_to_global_folder_lock()
        {
            lock (MediaFileOperationLock.ForSeries(1))
            {
                Subject.RecoverImports(_localEpisode.Series);
            }

            Mocker.GetMock<ISeriesFolderMoveService>().Verify(s => s.Recover(It.IsAny<Series>()), Times.Never());
        }

        [Test]
        public void should_retry_folder_recovery_if_move_became_pending_while_waiting()
        {
            Mocker.GetMock<ISeriesFolderMoveService>().SetupSequence(s => s.HasPendingMove(1)).Returns(true).Returns(false).Returns(false);

            Subject.RecoverImports(_localEpisode.Series);

            Mocker.GetMock<ISeriesFolderMoveService>().Verify(s => s.Recover(_localEpisode.Series), Times.Exactly(2));
        }

        [Test]
        public void should_fail_scoped_recovery_without_escalating_if_folder_recovery_is_pending()
        {
            Mocker.GetMock<ISeriesFolderMoveService>().Setup(s => s.HasPendingMove(1)).Returns(true);

            Assert.Throws<IOException>(() => Subject.RecoverFileOperations(_localEpisode.Series));

            Mocker.GetMock<ISeriesFolderMoveService>().Verify(s => s.Recover(It.IsAny<Series>()), Times.Never());
            AssertOldFileUnchanged();
        }

        [Test]
        public void should_not_delete_retargeted_download_source_after_staging()
        {
            PosixOnly();
            var originalDirectory = Path.Combine(TempFolder, "original-download");
            var replacementDirectory = Path.Combine(TempFolder, "replacement-download");
            Directory.CreateDirectory(originalDirectory);
            Directory.CreateDirectory(replacementDirectory);
            var originalSource = Path.Combine(originalDirectory, "download.mkv");
            File.Move(_localEpisode.Path, originalSource);
            var replacementSource = Path.Combine(replacementDirectory, "download.mkv");
            File.WriteAllText(replacementSource, "unrelated bytes");
            var sourceLink = Path.Combine(TempFolder, "download-link");
            Directory.CreateSymbolicLink(sourceLink, originalDirectory);
            _localEpisode.Path = Path.Combine(sourceLink, "download.mkv");
            _episodeFile.Path = _localEpisode.Path;
            Mocker.GetMock<IDiskTransferService>()
                .Setup(s => s.TransferFile(originalSource, It.IsAny<string>(), It.IsAny<TransferMode>(), false))
                .Returns<string, string, TransferMode, bool>((source, target, mode, overwrite) =>
                {
                    File.Copy(source, target);
                    Directory.Delete(sourceLink);
                    Directory.CreateSymbolicLink(sourceLink, replacementDirectory);
                    return mode;
                });

            Assert.Throws<IOException>(() => Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode));

            File.ReadAllText(originalSource).Should().Be("new episode bytes");
            File.ReadAllText(replacementSource).Should().Be("unrelated bytes");
            File.ReadAllText(_oldPath).Should().Be("old episode bytes");
            _links.Single().EpisodeFileId.Should().Be(1);
            Mocker.GetMock<IEpisodeTrackFileService>().Verify(s => s.ImportFile(It.IsAny<EpisodeFile>(), It.IsAny<List<EpisodeTrackFile>>()), Times.Never());
        }

        [Test]
        public void should_fail_before_rename_when_source_disappeared()
        {
            File.Delete(_oldPath);

            Assert.Throws<FileNotFoundException>(() => Subject.BeginRename(_localEpisode.Series, _oldFile, _destination));

            _links.Single().EpisodeFileId.Should().Be(1);
            File.Exists(_destination).Should().BeFalse();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_not_begin_rename_over_existing_destination(bool tracked)
        {
            File.WriteAllText(_destination, "other bytes");
            if (tracked)
            {
                _files.Add(new EpisodeFile { Id = 2, SeriesId = 1, RelativePath = "new.mp4" });
            }

            Assert.Throws<DestinationAlreadyExistsException>(() => Subject.BeginRename(_localEpisode.Series, _oldFile, _destination));

            File.ReadAllText(_destination).Should().Be("other bytes");
            File.ReadAllText(_oldPath).Should().Be("old episode bytes");
        }

        [Test]
        public void should_finish_committed_rename_receipt_without_removing_unrelated_receipt_files()
        {
            var receipt = Subject.BeginRename(_localEpisode.Series, _oldFile, _destination);
            File.Move(_oldPath, _destination);
            _oldFile.RelativePath = "new.mkv";
            var unrelated = Path.Combine(receipt, "operator-note.txt");
            File.WriteAllText(unrelated, "keep this note");

            Subject.FinishRename(_localEpisode.Series, receipt);

            File.Exists(Path.Combine(receipt, "rename.json")).Should().BeFalse();
            File.ReadAllText(unrelated).Should().Be("keep this note");
            File.ReadAllText(_destination).Should().Be("old episode bytes");
        }

        private void GivenImportProfileSignature()
        {
            var profile = new QualityProfile { Id = 1, Items = Qualities.QualityFixture.GetDefaultQualities(Quality.HDTV720p) };
            _tracks[0].QualityProfile = profile;
            _localEpisode.TargetQualityTrackSignatures = new Dictionary<int, string> { [1] = QualityTrackSnapshot.ProfileSignature(profile) };
        }

        [Test]
        public void should_import_when_validated_profile_and_associations_are_unchanged()
        {
            GivenImportProfileSignature();
            _localEpisode.ExpectedTrackFiles =
            [
                new EpisodeTrackFile { EpisodeId = 10, TrackId = 1, EpisodeFileId = 1 },
                new EpisodeTrackFile { EpisodeId = 11, TrackId = 1, EpisodeFileId = 99 },
                new EpisodeTrackFile { EpisodeId = 10, TrackId = 2, EpisodeFileId = 99 }
            ];

            Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode);

            File.ReadAllText(_destination).Should().Be("new episode bytes");
            _links.Single().EpisodeFileId.Should().Be(100);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_preserve_library_when_profile_signature_changed_or_disappeared(bool missing)
        {
            GivenImportProfileSignature();
            _localEpisode.TargetQualityTrackSignatures = missing ? new Dictionary<int, string>() : new Dictionary<int, string> { [1] = "old criteria" };

            Assert.Throws<InvalidOperationException>(() => Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode));

            AssertOldFileUnchanged();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void explicit_manual_or_proven_legacy_import_should_keep_existing_override_behavior(bool manual)
        {
            GivenImportProfileSignature();
            _localEpisode.TargetQualityTrackSignatures[1] = "old criteria";
            _localEpisode.ManualImport = manual;
            _localEpisode.LegacyQualityTrackTarget = !manual;

            Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode);

            File.ReadAllText(_destination).Should().Be("new episode bytes");
        }

        [Test]
        public void should_reject_stale_decision_when_previous_reference_was_removed()
        {
            _localEpisode.ExpectedTrackFiles = [new EpisodeTrackFile { EpisodeId = 10, TrackId = 1, EpisodeFileId = 1 }];
            _links.Clear();

            Assert.Throws<InvalidOperationException>(() => Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode));

            File.ReadAllText(_oldPath).Should().Be("old episode bytes");
            File.ReadAllText(_localEpisode.Path).Should().Be("new episode bytes");
            _links.Should().BeEmpty();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_reject_missing_or_truncated_staging_bytes_before_unlinking_library(bool truncated)
        {
            Mocker.GetMock<IDiskTransferService>()
                .Setup(s => s.TransferFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TransferMode>(), false))
                .Returns<string, string, TransferMode, bool>((source, target, mode, overwrite) =>
                {
                    if (truncated)
                    {
                        File.WriteAllText(target, "bad");
                    }

                    return mode;
                });

            Assert.Throws<IOException>(() => Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode));

            AssertOldFileUnchanged();
        }

        [Test]
        public void should_validate_backup_copy_even_when_provider_reports_success()
        {
            _destination = _oldPath;
            Mocker.GetMock<IDiskTransferService>().Setup(s => s.TransferFile(_oldPath, It.IsAny<string>(), TransferMode.Copy, false))
                .Returns<string, string, TransferMode, bool>((source, target, mode, overwrite) =>
                {
                    File.WriteAllText(target, "bad");
                    return mode;
                });

            Assert.Throws<IOException>(() => Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode));

            AssertOldFileUnchanged();
        }

        [Test]
        public void should_reject_script_destination_owned_by_another_file()
        {
            var retained = Path.Combine(_localEpisode.Series.Path, "retained.mkv");
            File.WriteAllText(retained, "retained bytes");
            _files.Add(new EpisodeFile { Id = 2, SeriesId = 1, RelativePath = "retained.mkv" });
            Mocker.GetMock<IMoveEpisodeFiles>().Setup(s => s.MoveEpisodeFile(_episodeFile, _localEpisode)).Returns(() =>
            {
                _localEpisode.ScriptImported = true;
                _episodeFile.RelativePath = "retained.mkv";
                return _episodeFile;
            });

            Assert.Throws<DestinationAlreadyExistsException>(() => Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode));

            AssertOldFileUnchanged();
            File.ReadAllText(retained).Should().Be("retained bytes");
        }

        [Test]
        public void should_fail_if_transfer_acknowledges_missing_destination()
        {
            Mocker.GetMock<IMoveEpisodeFiles>().Setup(s => s.MoveEpisodeFile(_episodeFile, _localEpisode)).Returns(() =>
            {
                File.Delete(_episodeFile.Path);
                _episodeFile.RelativePath = "new.mkv";
                return _episodeFile;
            });

            Assert.Throws<IOException>(() => Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode));

            AssertOldFileUnchanged();
        }

        [Test]
        public void failed_database_commit_should_restore_source_consumed_by_import_script()
        {
            _failCommit = true;
            Mocker.GetMock<IMoveEpisodeFiles>().Setup(s => s.MoveEpisodeFile(_episodeFile, _localEpisode)).Returns(() =>
            {
                File.Move(_localEpisode.Path, _destination);
                _episodeFile.RelativePath = "new.mkv";
                _localEpisode.ScriptImported = true;
                return _episodeFile;
            });

            Assert.Throws<IOException>(() => Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode));

            AssertOldFileUnchanged();
            File.Exists(_destination).Should().BeFalse();
        }

        [Test]
        public void first_import_with_hardlink_preference_should_not_invent_replaced_files()
        {
            _files.Clear();
            _links.Clear();
            File.Delete(_oldPath);
            Mocker.GetMock<IConfigService>().SetupGet(s => s.CopyUsingHardlinks).Returns(true);

            var result = Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode, true);

            result.OldFiles.Should().BeEmpty();
            _localEpisode.IsUpgrade.Should().BeFalse();
            File.ReadAllText(_destination).Should().Be("new episode bytes");
            File.ReadAllText(_localEpisode.Path).Should().Be("new episode bytes");
            Mocker.GetMock<IDiskTransferService>().Verify(s => s.TransferFile(It.IsAny<string>(), It.IsAny<string>(), TransferMode.HardLinkOrCopy, false), Times.Once());
        }

        private string CreateRecoveryReceipt()
        {
            var work = Path.Combine(_localEpisode.Series.Path, ".sonarr-import", "recovery-case");
            Directory.CreateDirectory(Path.Combine(work, "incoming"));
            return WriteImportReceipt(work, new UpgradeMediaFileService.ImportJournal
            {
                SeriesId = 1,
                SourcePath = _localEpisode.Path,
                ResolvedSourcePath = _localEpisode.Path,
                DestinationPath = _destination,
                StagedPath = Path.Combine(work, "incoming", "download.mkv"),
                OldFileIds = [1],
                OldFiles = [new EpisodeFile { Id = 1, SeriesId = 1, RelativePath = "old.mkv", Path = _oldPath }],
                OldLinks = [new EpisodeTrackFile { EpisodeId = 10, TrackId = 1, EpisodeFileId = 1 }],
                EpisodeIds = [10],
                TrackIds = [1],
                DateAdded = _episodeFile.DateAdded
            });
        }

        [TestCase("null")]
        [TestCase("seriesId")]
        [TestCase("oldFileIds")]
        public void malformed_import_receipt_should_not_change_any_library_file(string invalid)
        {
            var directory = CreateRecoveryReceipt();
            var path = Path.Combine(directory, "import.json");
            var node = JsonNode.Parse(File.ReadAllText(path));
            if (invalid == "null")
            {
                File.WriteAllText(path, "null");
            }
            else
            {
                node[invalid] = invalid == "seriesId" ? JsonValue.Create(999) : null;
                File.WriteAllText(path, node.ToJsonString());
            }

            Assert.Throws<IOException>(() => Subject.RecoverImports(_localEpisode.Series));

            AssertOldFileUnchanged();
            File.Exists(path).Should().BeTrue();
        }

        [TestCase("oldFiles")]
        [TestCase("oldLinks")]
        public void rollback_with_empty_optional_snapshot_should_preserve_files_and_ownership(string collection)
        {
            var directory = CreateRecoveryReceipt();
            var path = Path.Combine(directory, "import.json");
            var node = JsonNode.Parse(File.ReadAllText(path));
            node[collection] = null;
            File.WriteAllText(path, node.ToJsonString());
            var journal = Json.Deserialize<UpgradeMediaFileService.ImportJournal>(File.ReadAllText(path));
            var snapshotCount = collection == "oldFiles" ? journal.OldFiles.Count : journal.OldLinks.Count;
            snapshotCount.Should().Be(0);

            Subject.RecoverImports(_localEpisode.Series);
            Subject.RecoverImports(_localEpisode.Series);

            AssertOldFileUnchanged();
            _files.Should().ContainSingle().Which.Should().BeSameAs(_oldFile);
            _links.Should().ContainSingle();
            File.Exists(_destination).Should().BeFalse();
            File.Exists(path).Should().BeFalse();
            Mocker.GetMock<IEpisodeTrackFileService>().Verify(s => s.ImportFile(It.IsAny<EpisodeFile>(), It.IsAny<List<EpisodeTrackFile>>()), Times.Never());
        }

        [Test]
        public void receipt_for_another_staging_directory_should_not_delete_that_directory()
        {
            var directory = CreateRecoveryReceipt();
            var path = Path.Combine(directory, "import.json");
            var foreign = Path.Combine(_localEpisode.Series.Path, "unrelated");
            Directory.CreateDirectory(foreign);
            File.WriteAllText(Path.Combine(foreign, "keep.txt"), "keep");
            var node = JsonNode.Parse(File.ReadAllText(path));
            node["workDirectory"] = foreign;
            File.WriteAllText(path, node.ToJsonString());

            Assert.Throws<IOException>(() => Subject.RecoverImports(_localEpisode.Series));

            File.ReadAllText(Path.Combine(foreign, "keep.txt")).Should().Be("keep");
            AssertOldFileUnchanged();
        }

        [TestCase("seriesId")]
        [TestCase("id")]
        public void foreign_old_file_snapshot_should_not_authorize_cleanup(string invalid)
        {
            var directory = CreateRecoveryReceipt();
            var path = Path.Combine(directory, "import.json");
            var node = JsonNode.Parse(File.ReadAllText(path));
            node["oldFiles"][0][invalid] = 999;
            File.WriteAllText(path, node.ToJsonString());

            Assert.Throws<IOException>(() => Subject.RecoverImports(_localEpisode.Series));

            AssertOldFileUnchanged();
            File.Exists(path).Should().BeTrue();
        }

        [TestCase("null")]
        [TestCase("seriesId")]
        [TestCase("episodeFileId")]
        [TestCase("sourceRelativePath")]
        [TestCase("destinationRelativePath")]
        public void malformed_rename_receipt_should_preserve_media_and_receipt(string invalid)
        {
            var directory = Subject.BeginRename(_localEpisode.Series, _oldFile, _destination);
            var path = Path.Combine(directory, "rename.json");
            var node = JsonNode.Parse(File.ReadAllText(path));
            if (invalid == "null")
            {
                File.WriteAllText(path, "null");
            }
            else if (invalid == "seriesId")
            {
                node[invalid] = 999;
                File.WriteAllText(path, node.ToJsonString());
            }
            else if (invalid == "episodeFileId")
            {
                node[invalid] = 0;
                File.WriteAllText(path, node.ToJsonString());
            }
            else
            {
                node[invalid] = null;
                File.WriteAllText(path, node.ToJsonString());
            }

            Assert.Throws<IOException>(() => Subject.RecoverImports(_localEpisode.Series));

            AssertOldFileUnchanged();
            File.Exists(path).Should().BeTrue();
        }

        [Test]
        public void rollback_should_not_replace_valid_new_bytes_with_damaged_backup()
        {
            _destination = _oldPath;
            Mocker.GetMock<IEpisodeTrackFileService>().Setup(s => s.ImportFile(_episodeFile, It.IsAny<List<EpisodeTrackFile>>()))
                .Callback(() =>
                {
                    var backup = Directory.GetFiles(Path.Combine(_localEpisode.Series.Path, ".sonarr-import"), "old.mkv", SearchOption.AllDirectories).Single();
                    File.WriteAllText(backup, "damaged");
                })
                .Throws(new IOException("Database unavailable"));

            Assert.Throws<IOException>(() => Subject.UpgradeEpisodeFile(_episodeFile, _localEpisode));

            File.ReadAllText(_destination).Should().Be("new episode bytes");
            File.ReadAllText(_localEpisode.Path).Should().Be("new episode bytes");
            _links.Single().EpisodeFileId.Should().Be(1);
            Directory.GetFiles(Path.Combine(TempFolder, "appdata", "MediaFileRecovery"), "import.json", SearchOption.AllDirectories).Should().ContainSingle();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void recovery_should_preserve_receipt_if_old_database_file_identity_changed(bool foreignSeries)
        {
            var directory = CreateRecoveryReceipt();
            var path = Path.Combine(directory, "import.json");
            var node = JsonNode.Parse(File.ReadAllText(path));
            node["committedFileId"] = 100;
            File.WriteAllText(path, node.ToJsonString());
            if (foreignSeries)
            {
                _oldFile.SeriesId = 2;
            }
            else
            {
                _oldFile.RelativePath = "different.mkv";
            }

            Assert.Throws<IOException>(() => Subject.RecoverImports(_localEpisode.Series));

            File.ReadAllText(_oldPath).Should().Be("old episode bytes");
            File.Exists(path).Should().BeTrue();
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Delete(It.IsAny<EpisodeFile>(), It.IsAny<DeleteMediaFileReason>()), Times.Never());
        }
    }
}
