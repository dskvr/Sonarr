using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FizzWare.NBuilder;
using FluentAssertions;
using FluentValidation;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.AutoTagging;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Core.Tv.Events;

namespace NzbDrone.Core.Test.TvTests
{
    [TestFixture]
    public partial class SeriesFolderMoveServiceFixture : DbTest<SeriesFolderMoveService, Series>
    {
        private Series _series;
        private Series _stored;
        private string _source;
        private string _destination;
        private string _journalPath;
        private Dictionary<string, string> _contents;
        private bool _failCommit;

        [SetUp]
        public void Setup()
        {
            _source = Path.Combine(TempFolder, "source", "Series");
            _destination = Path.Combine(TempFolder, "destination", "Series");
            var appData = Path.Combine(TempFolder, "appdata");
            _journalPath = Path.Combine(appData, "MediaFileRecovery", "series", "1.json");
            Directory.CreateDirectory(appData);
            Directory.CreateDirectory(Path.Combine(_source, "Season 1"));
            Directory.CreateDirectory(Path.Combine(_source, "Season 2"));
            _contents = new Dictionary<string, string>
            {
                [Path.Combine("Season 1", "episode-1080p.mkv")] = "HD version bytes",
                [Path.Combine("Season 1", "episode-2160p.mkv")] = "UHD version bytes",
                [Path.Combine("Season 1", "episode-1080p.en.srt")] = "Subtitle bytes",
                ["poster.jpg"] = "Artwork bytes"
            };
            foreach (var file in _contents)
            {
                File.WriteAllText(Path.Combine(_source, file.Key), file.Value);
            }

            var profile = Db.Insert(new QualityProfile { Name = "HD", Items = new List<QualityProfileQualityItem>() });
            var secondaryProfile = Db.Insert(new QualityProfile { Name = "UHD", Items = new List<QualityProfileQualityItem>() });
            _series = Db.Insert(Builder<Series>.CreateNew().With(s => s.Path = _source).With(s => s.QualityProfileId = profile.Id).BuildNew());
            var tracks = new[]
            {
                Db.Insert(new SeriesQualityTrack { SeriesId = _series.Id, QualityProfileId = profile.Id, IsPrimary = true, Enabled = true }),
                Db.Insert(new SeriesQualityTrack { SeriesId = _series.Id, QualityProfileId = secondaryProfile.Id, Enabled = true })
            };
            var episode = Db.Insert(Builder<Episode>.CreateNew().With(e => e.SeriesId = _series.Id).With(e => e.EpisodeFileId = 0).BuildNew());
            var files = new List<EpisodeFile>();
            foreach (var pair in new[] { (tracks[0], "episode-1080p.mkv"), (tracks[1], "episode-2160p.mkv") })
            {
                var file = Db.Insert(Builder<EpisodeFile>.CreateNew().With(f => f.SeriesId = _series.Id)
                    .With(f => f.RelativePath = Path.Combine("Season 1", pair.Item2)).With(f => f.Quality = new QualityModel(Quality.HDTV1080p))
                    .With(f => f.Languages = new List<Language> { Language.English }).BuildNew());
                Db.Insert(new EpisodeTrackFile { EpisodeId = episode.Id, TrackId = pair.Item1.Id, EpisodeFileId = file.Id });
                files.Add(file);
            }

            episode.EpisodeFileId = files[0].Id;
            Db.Update(episode);
            _stored = _series.Clone();
            _failCommit = false;
            Mocker.GetMock<IAppFolderInfo>().SetupGet(f => f.AppDataFolder).Returns(appData);
            var repository = Mocker.Resolve<SeriesRepository>();
            Mocker.GetMock<ISeriesRepository>().Setup(r => r.Get(It.IsAny<int>())).Returns<int>(repository.Get);
            Mocker.GetMock<ISeriesRepository>().Setup(r => r.Get(It.IsAny<IEnumerable<int>>())).Returns<IEnumerable<int>>(repository.Get);
            Mocker.GetMock<ISeriesRepository>().Setup(r => r.Find(It.IsAny<int>())).Returns<int>(repository.Find);
            Mocker.GetMock<ISeriesRepository>().Setup(r => r.All()).Returns(repository.All);
            Mocker.GetMock<ISeriesRepository>().Setup(r => r.HasPathConflict(It.IsAny<int>(), It.IsAny<string>())).Returns<int, string>(repository.HasPathConflict);
            Mocker.GetMock<ISeriesRepository>().Setup(r => r.Insert(It.IsAny<Series>())).Returns<Series>(repository.Insert);
            Mocker.GetMock<ISeriesRepository>().Setup(r => r.InsertMany(It.IsAny<IList<Series>>())).Callback<IList<Series>>(repository.InsertMany);
            Mocker.GetMock<ISeriesRepository>().Setup(r => r.Update(It.IsAny<Series>())).Returns<Series>(repository.Update);
            Mocker.GetMock<ISeriesRepository>().Setup(r => r.UpdateMany(It.IsAny<IList<Series>>())).Callback<IList<Series>>(repository.UpdateMany);
            Mocker.GetMock<ISeriesRepository>().Setup(r => r.DeleteMany(It.IsAny<IEnumerable<int>>())).Callback<IEnumerable<int>>(repository.DeleteMany);
            Mocker.GetMock<ISeriesRepository>().Setup(r => r.UpdatePath(It.IsAny<int>(), It.IsAny<string>()))
                .Returns<int, string>((id, path) =>
                {
                    if (_failCommit)
                    {
                        throw new IOException("Database commit failed");
                    }

                    _stored = repository.UpdatePath(id, path).Clone();
                    return _stored.Clone();
                });
            Mocker.GetMock<IAutoTaggingService>().Setup(s => s.GetTagChanges(It.IsAny<Series>())).Returns(new AutoTaggingChanges());
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFilesBySeries(1)).Returns(() => Db.All<EpisodeFile>());

            Mocker.GetMock<IDiskProvider>().Setup(d => d.FolderExists(It.IsAny<string>())).Returns<string>(Directory.Exists);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FileExists(It.IsAny<string>())).Returns<string>(File.Exists);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.GetFileAttributes(It.IsAny<string>())).Returns<string>(File.GetAttributes);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.CreateFolder(It.IsAny<string>())).Callback<string>(path => Directory.CreateDirectory(path));
            Mocker.GetMock<IDiskProvider>().Setup(d => d.MoveFolder(It.IsAny<string>(), It.IsAny<string>())).Callback<string, string>(Directory.Move);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.MoveFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>())).Callback<string, string, bool>(File.Move);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.DeleteFolder(It.IsAny<string>(), It.IsAny<bool>())).Callback<string, bool>(Directory.Delete);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.DeleteFile(It.IsAny<string>())).Callback<string>(File.Delete);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.GetDirectories(It.IsAny<string>())).Returns<string>(Directory.GetDirectories);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.GetFiles(It.IsAny<string>(), It.IsAny<bool>()))
                .Returns<string, bool>((path, recursive) => Directory.GetFiles(path, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly));
            Mocker.GetMock<IDiskProvider>().Setup(d => d.OpenReadStream(It.IsAny<string>())).Returns<string>(File.OpenRead);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.ReadAllText(It.IsAny<string>())).Returns<string>(File.ReadAllText);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.WriteAllText(It.IsAny<string>(), It.IsAny<string>())).Callback<string, string>(File.WriteAllText);
            Mocker.GetMock<IDiskTransferService>().Setup(d => d.TransferFile(It.IsAny<string>(), It.IsAny<string>(), TransferMode.Copy, false))
                .Returns<string, string, TransferMode, bool>((source, destination, mode, overwrite) =>
                {
                    File.Copy(source, destination, overwrite);
                    return mode;
                });
        }

        private void RequireCopy()
        {
            var source = MediaFileRecoveryPaths.ResolveFilePath(_source);
            var destination = MediaFileRecoveryPaths.ResolveFilePath(_destination);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.MoveFolder(source, destination)).Throws(new IOException("Cross-device move"));
        }

        private void AssertContents(string root)
        {
            foreach (var file in _contents)
            {
                File.ReadAllText(Path.Combine(root, file.Key)).Should().Be(file.Value);
            }
        }

        private SeriesFolderMoveService.MoveJournal Journal(bool atomic, bool prepared = false, bool destinationExisted = false)
        {
            return new SeriesFolderMoveService.MoveJournal
            {
                SeriesId = 1,
                SourcePath = _source,
                DestinationPath = _destination,
                ResolvedSourcePath = MediaFileRecoveryPaths.ResolveFilePath(_source),
                ResolvedDestinationPath = MediaFileRecoveryPaths.ResolveFilePath(_destination),
                OperationId = Guid.NewGuid().ToString("N"),
                AtomicMove = atomic,
                Prepared = prepared,
                DestinationExisted = destinationExisted,
                Directories = atomic ? new List<string>() : Directory.GetDirectories(_source, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(_source, path)).ToList(),
                Files = atomic ? new List<SeriesFolderMoveService.MoveFile>() : _contents.Keys.Select(path => new SeriesFolderMoveService.MoveFile
                {
                    RelativePath = path,
                    Hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_source, path))))
                }).ToList()
            };
        }

        private void WriteJournal(SeriesFolderMoveService.MoveJournal journal)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_journalPath));
            File.WriteAllText(_journalPath, journal.ToJson());
        }

        private string Stage(SeriesFolderMoveService.MoveJournal journal)
        {
            var stage = Path.Combine(Path.GetDirectoryName(_destination), ".sonarr-move-" + journal.OperationId);
            Directory.CreateDirectory(stage);
            foreach (var directory in journal.Directories)
            {
                Directory.CreateDirectory(Path.Combine(stage, directory));
            }

            foreach (var file in journal.Files)
            {
                File.Copy(Path.Combine(_source, file.RelativePath), Path.Combine(stage, file.RelativePath));
            }

            return stage;
        }

        [Test]
        public void should_move_all_versions_and_sidecars_without_copying_on_same_volume()
        {
            Subject.Move(_series, _source, _destination);
            _stored.Path.Should().Be(_destination);
            AssertContents(_destination);
            Directory.Exists(_source).Should().BeFalse();
            Directory.Exists(Path.Combine(_destination, "Season 2")).Should().BeTrue();
            File.Exists(_journalPath).Should().BeFalse();
            Mocker.GetMock<IDiskProvider>().Verify(d => d.OpenReadStream(It.IsAny<string>()), Times.Never());
            Mocker.GetMock<IDiskTransferService>().Verify(d => d.TransferFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TransferMode>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void should_copy_safely_when_atomic_move_is_unavailable()
        {
            RequireCopy();
            Subject.Move(_series, _source, _destination);
            AssertContents(_destination);
            _stored.Path.Should().Be(_destination);
            Directory.Exists(_source).Should().BeFalse();
            File.Exists(_journalPath).Should().BeFalse();
        }

        [Test]
        public void should_merge_disjoint_destination_without_changing_existing_files()
        {
            Directory.CreateDirectory(_destination);
            var existing = Path.Combine(_destination, "existing.nfo");
            File.WriteAllText(existing, "Existing metadata");
            Subject.Move(_series, _source, _destination);
            AssertContents(_destination);
            File.ReadAllText(existing).Should().Be("Existing metadata");
            Directory.Exists(_source).Should().BeFalse();
        }

        [TestCase("poster.jpg")]
        [TestCase("POSTER.JPG")]
        public void should_reject_collisions_before_transferring_any_bytes(string name)
        {
            Directory.CreateDirectory(_destination);
            File.WriteAllText(Path.Combine(_destination, name), "Existing artwork");
            Assert.Throws<IOException>(() => Subject.Move(_series, _source, _destination));
            AssertContents(_source);
            File.ReadAllText(Path.Combine(_destination, name)).Should().Be("Existing artwork");
            _stored.Path.Should().Be(_source);
            Mocker.GetMock<IDiskTransferService>().Verify(d => d.TransferFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TransferMode>(), It.IsAny<bool>()), Times.Never());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_restore_original_folder_after_database_failure(bool copy)
        {
            if (copy)
            {
                RequireCopy();
            }

            _failCommit = true;
            Assert.Throws<IOException>(() => Subject.Move(_series, _source, _destination));
            AssertContents(_source);
            _stored.Path.Should().Be(_source);
            Directory.Exists(_destination).Should().BeFalse();
            File.Exists(_journalPath).Should().BeFalse();
        }

        [Test]
        public void should_preserve_source_when_copy_fails_after_partial_transfer()
        {
            RequireCopy();
            var count = 0;
            Mocker.GetMock<IDiskTransferService>().Setup(d => d.TransferFile(It.IsAny<string>(), It.IsAny<string>(), TransferMode.Copy, false))
                .Returns<string, string, TransferMode, bool>((source, destination, mode, overwrite) =>
                {
                    if (++count == 2)
                    {
                        File.WriteAllText(destination, "partial");
                        throw new IOException("Copy interrupted");
                    }

                    File.Copy(source, destination, overwrite);
                    return mode;
                });

            Assert.Throws<IOException>(() => Subject.Move(_series, _source, _destination));
            AssertContents(_source);
            _stored.Path.Should().Be(_source);
            Directory.Exists(_destination).Should().BeFalse();
            File.Exists(_journalPath).Should().BeFalse();
        }

        [Test]
        public void should_recover_atomic_move_before_database_commit_after_restart()
        {
            WriteJournal(Journal(true));
            Directory.CreateDirectory(Path.GetDirectoryName(_destination));
            Directory.Move(_source, _destination);
            Mocker.Resolve<SeriesFolderMoveService>().Recover(_series);
            AssertContents(_source);
            Directory.Exists(_destination).Should().BeFalse();
            _series.Path.Should().Be(_source);
            File.Exists(_journalPath).Should().BeFalse();
        }

        [Test]
        public void should_recover_partial_copy_after_restart()
        {
            var journal = Journal(false);
            var stage = Stage(journal);
            File.WriteAllText(Path.Combine(stage, journal.Files[0].RelativePath), "partial");
            WriteJournal(journal);
            Mocker.Resolve<SeriesFolderMoveService>().Recover(_series);
            AssertContents(_source);
            Directory.Exists(stage).Should().BeFalse();
            File.Exists(_journalPath).Should().BeFalse();
        }

        [Test]
        public void should_roll_back_partially_published_merge_after_restart()
        {
            var journal = Journal(false, true, true);
            var stage = Stage(journal);
            Directory.CreateDirectory(_destination);
            File.WriteAllText(Path.Combine(_destination, "existing.nfo"), "Unrelated metadata");
            var first = journal.Files[0].RelativePath;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_destination, first)));
            File.Move(Path.Combine(stage, first), Path.Combine(_destination, first));
            WriteJournal(journal);

            Mocker.Resolve<SeriesFolderMoveService>().Recover(_series);

            AssertContents(_source);
            File.Exists(Path.Combine(_destination, first)).Should().BeFalse();
            File.ReadAllText(Path.Combine(_destination, "existing.nfo")).Should().Be("Unrelated metadata");
            Directory.Exists(stage).Should().BeFalse();
        }

        [Test]
        public void should_finish_source_cleanup_after_committed_copy_restart()
        {
            var journal = Journal(false, true);
            var stage = Stage(journal);
            Directory.Move(stage, _destination);
            WriteJournal(journal);
            _stored.Path = _destination;
            Db.Update(_stored);
            Mocker.Resolve<SeriesFolderMoveService>().Recover(_series);
            AssertContents(_destination);
            Directory.Exists(_source).Should().BeFalse();
            _series.Path.Should().Be(_destination);
            File.Exists(_journalPath).Should().BeFalse();
        }

        [Test]
        public void should_preserve_unexpected_source_files_during_committed_move_cleanup()
        {
            var journal = Journal(false, true);
            var stage = Stage(journal);
            Directory.Move(stage, _destination);
            WriteJournal(journal);
            _stored.Path = _destination;
            Db.Update(_stored);
            File.WriteAllText(Path.Combine(_source, "new-file.txt"), "New external file");
            Assert.Throws<IOException>(() => Subject.Recover(_series));
            AssertContents(_source);
            AssertContents(_destination);
            File.ReadAllText(Path.Combine(_source, "new-file.txt")).Should().Be("New external file");
            File.Exists(_journalPath).Should().BeTrue();
        }

        [Test]
        public void should_move_case_only_folder_names()
        {
            var destination = Path.Combine(Path.GetDirectoryName(_source), "series");
            Subject.Move(_series, _source, destination);
            _stored.Path.Should().Be(destination);
            AssertContents(destination);
            File.Exists(_journalPath).Should().BeFalse();
        }

        [Test]
        public void should_report_case_only_move_failure_after_restoring_original_path()
        {
            var destination = Path.Combine(Path.GetDirectoryName(_source), "series");
            _failCommit = true;
            Assert.Throws<IOException>(() => Subject.Move(_series, _source, destination));
            AssertContents(_source);
            _stored.Path.Should().Be(_source);
            File.Exists(_journalPath).Should().BeFalse();
        }

        [Test]
        public void should_reject_recovery_paths_outside_series_folders()
        {
            var journal = Journal(false, true);
            journal.Files[0].RelativePath = Path.Combine("..", "foreign.mkv");
            WriteJournal(journal);
            Assert.Throws<IOException>(() => Subject.Recover(_series));
            AssertContents(_source);
            File.Exists(_journalPath).Should().BeTrue();
        }

        [Test]
        public void should_change_path_for_missing_empty_library_folder()
        {
            Directory.Delete(_source, true);
            foreach (var link in Db.All<EpisodeTrackFile>())
            {
                Db.Delete(link);
            }

            foreach (var file in Db.All<EpisodeFile>())
            {
                Db.Delete(file);
            }

            Subject.Move(_series, _source, _destination);
            _stored.Path.Should().Be(_destination);
        }

        [Test]
        public void should_preserve_path_when_folder_with_recorded_files_is_unavailable()
        {
            Directory.Delete(_source, true);
            Assert.Throws<IOException>(() => Subject.Move(_series, _source, _destination));
            _stored.Path.Should().Be(_source);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_preserve_file_and_track_ids_when_moving_all_versions(bool copy)
        {
            if (copy)
            {
                RequireCopy();
            }

            var files = Db.All<EpisodeFile>().Select(f => (f.Id, f.SeriesId, f.RelativePath)).ToList();
            var links = Db.All<EpisodeTrackFile>().Select(l => (l.Id, l.EpisodeId, l.TrackId, l.EpisodeFileId)).ToList();

            Subject.Move(_series, _source, _destination);

            Db.All<Series>().Single().Path.Should().Be(_destination);
            Db.All<EpisodeFile>().Select(f => (f.Id, f.SeriesId, f.RelativePath)).Should().BeEquivalentTo(files);
            Db.All<EpisodeTrackFile>().Select(l => (l.Id, l.EpisodeId, l.TrackId, l.EpisodeFileId)).Should().BeEquivalentTo(links);
            AssertContents(_destination);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_preserve_external_season_symlinks_without_copying_or_deleting_their_bytes(bool copy)
        {
            _destination = Path.Combine(TempFolder, "destination", "nested", "Series");
            if (copy)
            {
                RequireCopy();
            }

            var external = Path.Combine(TempFolder, "external-season");
            Directory.CreateDirectory(external);
            File.WriteAllText(Path.Combine(external, "external.mkv"), "Shared external episode");
            var sourceLink = Path.Combine(_source, "Season 3");
            Directory.CreateSymbolicLink(sourceLink, Path.GetRelativePath(_source, external));

            Subject.Move(_series, _source, _destination);

            var destinationLink = new DirectoryInfo(Path.Combine(_destination, "Season 3"));
            destinationLink.LinkTarget.Should().NotBeNull();
            destinationLink.ResolveLinkTarget(true).FullName.Should().Be(external);
            File.ReadAllText(Path.Combine(external, "external.mkv")).Should().Be("Shared external episode");
            File.ReadAllText(Path.Combine(destinationLink.FullName, "external.mkv")).Should().Be("Shared external episode");
            Directory.Exists(_source).Should().BeFalse();
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void should_retarget_internal_links_to_moved_counterparts(bool copy, bool absolute)
        {
            if (copy)
            {
                RequireCopy();
            }

            var sourceLink = Path.Combine(_source, "episode-link.mkv");
            var target = Path.Combine("Season 1", "episode-1080p.mkv");
            File.CreateSymbolicLink(sourceLink, absolute ? Path.Combine(_source, target) : target);
            Subject.Move(_series, _source, _destination);
            var destinationLink = new FileInfo(Path.Combine(_destination, "episode-link.mkv"));
            destinationLink.LinkTarget.Should().NotBeNull();
            destinationLink.ResolveLinkTarget(true).FullName.Should().Be(Path.Combine(_destination, target));
            File.ReadAllText(destinationLink.FullName).Should().Be("HD version bytes");
        }

        [Test]
        public void should_restore_original_symlink_targets_when_atomic_move_commit_fails()
        {
            _destination = Path.Combine(TempFolder, "destination", "nested", "Series");
            var external = Path.Combine(TempFolder, "external-season");
            Directory.CreateDirectory(external);
            File.WriteAllText(Path.Combine(external, "external.mkv"), "Shared external episode");
            var originalTarget = Path.GetRelativePath(_source, external);
            Directory.CreateSymbolicLink(Path.Combine(_source, "Season 3"), originalTarget);
            _failCommit = true;
            Assert.Throws<IOException>(() => Subject.Move(_series, _source, _destination));
            new DirectoryInfo(Path.Combine(_source, "Season 3")).LinkTarget.Should().Be(originalTarget);
            File.ReadAllText(Path.Combine(external, "external.mkv")).Should().Be("Shared external episode");
            _stored.Path.Should().Be(_source);
        }

        [Test]
        public void should_recover_atomic_move_after_database_commit_without_reverting_path()
        {
            WriteJournal(Journal(true));
            Directory.CreateDirectory(Path.GetDirectoryName(_destination));
            Directory.Move(_source, _destination);
            _stored.Path = _destination;
            Db.Update(_stored);

            Mocker.Resolve<SeriesFolderMoveService>().Recover(_series);

            AssertContents(_destination);
            Directory.Exists(_source).Should().BeFalse();
            _series.Path.Should().Be(_destination);
            File.Exists(_journalPath).Should().BeFalse();
        }

        [Test]
        public void should_recover_interrupted_source_cleanup_without_losing_destination_files()
        {
            RequireCopy();
            var blocked = Path.Combine(_source, "Season 1", "episode-2160p.mkv");
            Mocker.GetMock<IDiskProvider>().Setup(d => d.DeleteFile(blocked)).Throws(new IOException("Cleanup interrupted"));
            Assert.Throws<IOException>(() => Subject.Move(_series, _source, _destination));
            _stored.Path.Should().Be(_destination);
            AssertContents(_destination);
            File.Exists(_journalPath).Should().BeTrue();

            Mocker.GetMock<IDiskProvider>().Setup(d => d.DeleteFile(blocked)).Callback<string>(File.Delete);
            Mocker.Resolve<SeriesFolderMoveService>().Recover(_series);

            AssertContents(_destination);
            Directory.Exists(_source).Should().BeFalse();
            File.Exists(_journalPath).Should().BeFalse();
        }

        [Test]
        public void should_not_overwrite_or_delete_files_created_at_destination_during_publish()
        {
            Directory.CreateDirectory(_destination);
            var collision = Path.Combine(_destination, "Season 1", "episode-1080p.mkv");
            Mocker.GetMock<IDiskProvider>().Setup(d => d.MoveFile(It.IsAny<string>(), collision, false))
                .Callback<string, string, bool>((source, destination, overwrite) =>
                {
                    File.WriteAllText(destination, "Concurrent destination file");
                    File.Move(source, destination, overwrite);
                });

            Assert.Throws<IOException>(() => Subject.Move(_series, _source, _destination));

            AssertContents(_source);
            File.ReadAllText(collision).Should().Be("Concurrent destination file");
            _stored.Path.Should().Be(_source);
            File.Exists(_journalPath).Should().BeFalse();
        }

        [Test]
        public void should_preserve_files_when_source_link_target_changes_during_copy()
        {
            RequireCopy();
            var first = Path.Combine(TempFolder, "external-one");
            var second = Path.Combine(TempFolder, "external-two");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            File.WriteAllText(Path.Combine(first, "episode.mkv"), "First external bytes");
            File.WriteAllText(Path.Combine(second, "episode.mkv"), "Second external bytes");
            var link = Path.Combine(_source, "Season 3");
            Directory.CreateSymbolicLink(link, first);
            var swapped = false;
            Mocker.GetMock<IDiskTransferService>().Setup(d => d.TransferFile(It.IsAny<string>(), It.IsAny<string>(), TransferMode.Copy, false))
                .Returns<string, string, TransferMode, bool>((source, destination, mode, overwrite) =>
                {
                    File.Copy(source, destination, overwrite);
                    if (!swapped)
                    {
                        Directory.Delete(link);
                        Directory.CreateSymbolicLink(link, second);
                        swapped = true;
                    }

                    return mode;
                });

            Assert.Throws<IOException>(() => Subject.Move(_series, _source, _destination));

            AssertContents(_source);
            File.ReadAllText(Path.Combine(first, "episode.mkv")).Should().Be("First external bytes");
            File.ReadAllText(Path.Combine(second, "episode.mkv")).Should().Be("Second external bytes");
            new DirectoryInfo(link).LinkTarget.Should().Be(second);
            _stored.Path.Should().Be(_source);
        }

        [Test]
        public void should_keep_internal_links_correct_when_source_parent_is_a_symlink()
        {
            var parentLink = Path.Combine(TempFolder, "library-alias");
            Directory.CreateSymbolicLink(parentLink, Path.GetDirectoryName(_source));
            _source = Path.Combine(parentLink, "Series");
            _stored.Path = _source;
            _series.Path = _source;
            Db.Update(_stored);
            RequireCopy();
            var target = Path.Combine("Season 1", "episode-1080p.mkv");
            File.CreateSymbolicLink(Path.Combine(_source, "episode-link.mkv"), target);

            Subject.Move(_series, _source, _destination);

            var destinationLink = new FileInfo(Path.Combine(_destination, "episode-link.mkv"));
            destinationLink.ResolveLinkTarget(true).FullName.Should().Be(Path.Combine(_destination, target));
            File.ReadAllText(destinationLink.FullName).Should().Be("HD version bytes");
        }

        [Test]
        public void should_restore_original_link_entry_after_interrupted_atomic_link_swap()
        {
            _destination = Path.Combine(TempFolder, "destination", "nested", "Series");
            var external = Path.Combine(TempFolder, "shared-season");
            Directory.CreateDirectory(external);
            File.WriteAllText(Path.Combine(external, "episode.mkv"), "Shared bytes");
            var original = Path.GetRelativePath(_source, external);
            var replacement = Path.GetRelativePath(_destination, external);
            Directory.CreateSymbolicLink(Path.Combine(_source, "Season 3"), original);
            var journal = Journal(true);
            journal.Links.Add(new SeriesFolderMoveService.MoveLink
            {
                RelativePath = "Season 3",
                OriginalTarget = original,
                DestinationTarget = replacement,
                ResolvedTarget = external,
                IsDirectory = true
            });
            WriteJournal(journal);
            Directory.CreateDirectory(Path.GetDirectoryName(_destination));
            Directory.Move(_source, _destination);
            var moved = Path.Combine(_destination, "Season 3");
            Directory.Move(moved, moved + ".sonarr-link-backup-" + journal.OperationId);
            Directory.CreateSymbolicLink(moved + ".sonarr-link-new-" + journal.OperationId, replacement);

            Mocker.Resolve<SeriesFolderMoveService>().Recover(_series);

            new DirectoryInfo(Path.Combine(_source, "Season 3")).LinkTarget.Should().Be(original);
            File.ReadAllText(Path.Combine(external, "episode.mkv")).Should().Be("Shared bytes");
            File.Exists(_journalPath).Should().BeFalse();
        }

        [Test]
        public void should_keep_original_link_when_replacement_link_cannot_be_created()
        {
            _destination = Path.Combine(TempFolder, "destination", "nested", "Series");
            var external = Path.Combine(TempFolder, "shared-season");
            Directory.CreateDirectory(external);
            File.WriteAllText(Path.Combine(external, "episode.mkv"), "Shared bytes");
            var original = Path.GetRelativePath(_source, external);
            Directory.CreateSymbolicLink(Path.Combine(_source, "Season 3"), original);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.MoveFolder(_source, _destination))
                .Callback<string, string>((source, destination) =>
                {
                    Directory.Move(source, destination);
                    var journal = Json.Deserialize<SeriesFolderMoveService.MoveJournal>(File.ReadAllText(_journalPath));
                    File.WriteAllText(Path.Combine(destination, "Season 3.sonarr-link-new-" + journal.OperationId), "Unexpected file");
                });

            Assert.Throws<IOException>(() => Subject.Move(_series, _source, _destination));

            new DirectoryInfo(Path.Combine(_source, "Season 3")).LinkTarget.Should().Be(original);
            File.ReadAllText(Path.Combine(external, "episode.mkv")).Should().Be("Shared bytes");
            Directory.GetFiles(_source, "*.sonarr-link-new-*").Should().ContainSingle();
            File.Exists(_journalPath).Should().BeTrue();
        }

        [Test]
        public void should_reject_destination_claimed_by_an_earlier_queued_series_move()
        {
            var otherPath = Path.Combine(TempFolder, "other", "Other Series");
            Directory.CreateDirectory(otherPath);
            File.WriteAllText(Path.Combine(otherPath, "different.mkv"), "Other series bytes");
            var other = Db.Insert(Builder<Series>.CreateNew().With(s => s.TvdbId = 99).With(s => s.TitleSlug = "other-series")
                .With(s => s.Path = otherPath).With(s => s.QualityProfileId = _series.QualityProfileId).BuildNew());
            Db.Insert(new SeriesQualityTrack { SeriesId = other.Id, QualityProfileId = other.QualityProfileId, IsPrimary = true, Enabled = true });

            Subject.Move(_series, _source, _destination);

            Assert.Throws<IOException>(() => Subject.Move(other, otherPath, _destination));
            Db.All<Series>().Single(s => s.Id == other.Id).Path.Should().Be(otherPath);
            File.ReadAllText(Path.Combine(otherPath, "different.mkv")).Should().Be("Other series bytes");
            AssertContents(_destination);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_move_a_series_root_link_without_copying_its_shared_target(bool copy)
        {
            var external = Path.Combine(TempFolder, "actual-series");
            Directory.Move(_source, external);
            Directory.CreateSymbolicLink(_source, Path.GetRelativePath(Path.GetDirectoryName(_source), external));
            _destination = Path.Combine(TempFolder, "destination", "nested", "Series");
            if (copy)
            {
                RequireCopy();
            }

            Subject.Move(_series, _source, _destination);

            new DirectoryInfo(_destination).ResolveLinkTarget(true).FullName.Should().Be(external);
            AssertContents(external);
            AssertContents(_destination);
            new DirectoryInfo(_source).LinkTarget.Should().BeNull();
            _stored.Path.Should().Be(_destination);
            Mocker.GetMock<IDiskTransferService>().Verify(d => d.TransferFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TransferMode>(), It.IsAny<bool>()), Times.Never());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_restore_root_link_on_move_commit_failure_without_changing_shared_bytes(bool copy)
        {
            var external = Path.Combine(TempFolder, "actual-series");
            Directory.Move(_source, external);
            var target = Path.GetRelativePath(Path.GetDirectoryName(_source), external);
            Directory.CreateSymbolicLink(_source, target);
            _destination = Path.Combine(TempFolder, "destination", "nested", "Series");
            if (copy)
            {
                RequireCopy();
            }

            _failCommit = true;
            Assert.Throws<IOException>(() => Subject.Move(_series, _source, _destination));

            new DirectoryInfo(_source).LinkTarget.Should().Be(target);
            AssertContents(external);
            _stored.Path.Should().Be(_source);
        }

        private void GivenInterruptedAtomicMove()
        {
            WriteJournal(Journal(true));
            Directory.CreateDirectory(Path.GetDirectoryName(_destination));
            Directory.Move(_source, _destination);
            Mocker.SetConstant<ISeriesFolderMoveService>(Subject);
        }

        private Series NewSeries(string path)
        {
            return Builder<Series>.CreateNew().With(s => s.TvdbId = 99).With(s => s.TitleSlug = Guid.NewGuid().ToString("N"))
                .With(s => s.Path = path).With(s => s.QualityProfileId = _series.QualityProfileId).BuildNew();
        }

        [Test]
        public void should_recover_pending_move_before_another_series_claims_its_destination()
        {
            GivenInterruptedAtomicMove();
            var created = Mocker.Resolve<SeriesService>().AddSeries(NewSeries(_destination));

            created.Path.Should().Be(_destination);
            Db.All<Series>().Single(s => s.Id == _series.Id).Path.Should().Be(_source);
            AssertContents(_source);
            Directory.Exists(_destination).Should().BeFalse();
            File.Exists(_journalPath).Should().BeFalse();
        }

        [Test]
        public void should_recover_pending_move_before_a_no_move_path_edit_claims_its_destination()
        {
            var other = Mocker.GetMock<ISeriesRepository>().Object.Insert(NewSeries(Path.Combine(TempFolder, "other-series")));
            GivenInterruptedAtomicMove();
            other.Path = _destination;

            Mocker.Resolve<SeriesService>().UpdateSeries(other);

            Db.All<Series>().Single(s => s.Id == other.Id).Path.Should().Be(_destination);
            AssertContents(_source);
            Directory.Exists(_destination).Should().BeFalse();
            File.Exists(_journalPath).Should().BeFalse();
        }

        [Test]
        public void should_recover_pending_move_before_deleting_series()
        {
            GivenInterruptedAtomicMove();
            Mocker.Resolve<SeriesService>().DeleteSeries(new List<int> { _series.Id }, true, false);

            AssertContents(_source);
            Directory.Exists(_destination).Should().BeFalse();
            Db.All<Series>().Should().BeEmpty();
            File.Exists(_journalPath).Should().BeFalse();
            Mocker.GetMock<IEventAggregator>().Verify(e => e.PublishEvent(It.Is<SeriesDeletedEvent>(message => message.Series.Single().Path == _source)), Times.Once());
        }

        [Test]
        public void should_leave_unrelated_offline_move_pending_when_editing_another_path()
        {
            var journal = Journal(true);
            journal.SeriesId = 999;
            journal.SourcePath = Path.Combine(TempFolder, "offline", "source");
            journal.DestinationPath = Path.Combine(TempFolder, "offline", "destination");
            journal.ResolvedSourcePath = journal.SourcePath;
            journal.ResolvedDestinationPath = journal.DestinationPath;
            var path = Path.Combine(Path.GetDirectoryName(_journalPath), "999.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, journal.ToJson());

            Subject.RecoverPending(new[] { _series.Id }, new[] { _destination });

            File.Exists(path).Should().BeTrue();
            AssertContents(_source);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_serialize_new_path_claims_with_an_active_folder_move(bool editExisting)
        {
            var candidate = NewSeries(Path.Combine(TempFolder, "other-series"));
            if (editExisting)
            {
                Mocker.GetMock<ISeriesRepository>().Object.Insert(candidate);
            }

            candidate.Path = _destination;
            Mocker.SetConstant<ISeriesFolderMoveService>(Subject);
            var seriesService = Mocker.Resolve<SeriesService>();
            RequireCopy();
            using var transferring = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var writerStarted = new ManualResetEventSlim();
            Mocker.GetMock<IDiskTransferService>().Setup(d => d.TransferFile(It.IsAny<string>(), It.IsAny<string>(), TransferMode.Copy, false))
                .Returns<string, string, TransferMode, bool>((source, destination, mode, overwrite) =>
                {
                    transferring.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException("Move test did not release transfer");
                    }

                    File.Copy(source, destination, overwrite);
                    return mode;
                });
            var move = Task.Run(() => Subject.Move(_series, _source, _destination));
            Task<Exception> writer;
            try
            {
                transferring.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                writer = Task.Run(() =>
                {
                    writerStarted.Set();
                    try
                    {
                        if (editExisting)
                        {
                            seriesService.UpdateSeries(candidate);
                        }
                        else
                        {
                            seriesService.AddSeries(candidate);
                        }

                        return null;
                    }
                    catch (Exception exception)
                    {
                        return exception;
                    }
                });
                writerStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                writer.Wait(TimeSpan.FromMilliseconds(100)).Should().BeFalse();
            }
            finally
            {
                release.Set();
            }

            move.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            move.GetAwaiter().GetResult();
            writer.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            writer.GetAwaiter().GetResult().Should().BeOfType<ValidationException>();
            Db.All<Series>().Count(s => s.Path == _destination).Should().Be(1);
            AssertContents(_destination);
        }
    }
}
