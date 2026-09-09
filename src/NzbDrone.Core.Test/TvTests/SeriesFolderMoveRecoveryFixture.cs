using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Tv;
using NzbDrone.Core.Tv.Events;

namespace NzbDrone.Core.Test.TvTests
{
    public partial class SeriesFolderMoveServiceFixture
    {
        private static string OperationStage(SeriesFolderMoveService.MoveJournal journal)
        {
            return Path.Combine(Path.GetDirectoryName(journal.DestinationPath), ".sonarr-move-" + journal.OperationId);
        }

        private void AssertOriginalLibraryAndReceipt()
        {
            AssertContents(_source);
            Db.All<Series>().Single(s => s.Id == _series.Id).Path.Should().Be(_stored.Path);
            File.Exists(_journalPath).Should().BeTrue();
        }

        [Test]
        public void should_ignore_a_move_to_its_current_folder()
        {
            Subject.Move(_series, _source, _source);
            AssertContents(_source);
            File.Exists(_journalPath).Should().BeFalse();
            Mocker.GetMock<IDiskProvider>().Verify(d => d.MoveFolder(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void should_reject_stale_move_intent_after_path_changed()
        {
            _stored.Path = Path.Combine(TempFolder, "new-location");
            Db.Update(_stored);
            Assert.Throws<IOException>(() => Subject.Move(_series, _source, _destination));
            AssertContents(_source);
            Directory.Exists(_destination).Should().BeFalse();
        }

        [TestCase("parent")]
        [TestCase("child")]
        [TestCase("filesystem-root")]
        [TestCase("file")]
        [TestCase("link")]
        public void should_reject_unsafe_destination_topologies_before_moving(string destinationType)
        {
            var destination = _destination;
            switch (destinationType)
            {
                case "parent":
                    destination = Path.GetDirectoryName(_source);
                    break;
                case "child":
                    destination = Path.Combine(_source, "nested");
                    break;
                case "filesystem-root":
                    destination = Path.GetPathRoot(_source);
                    break;
                case "file":
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    File.WriteAllText(destination, "Foreign file");
                    break;
                case "link":
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    Directory.CreateSymbolicLink(destination, _source);
                    break;
            }

            Assert.Throws<IOException>(() => Subject.Move(_series, _source, destination));
            AssertContents(_source);
            File.Exists(_journalPath).Should().BeFalse();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_reject_root_link_moves_into_the_shared_target(bool descendant)
        {
            var shared = Path.Combine(TempFolder, "shared-series");
            Directory.Move(_source, shared);
            Directory.CreateSymbolicLink(_source, shared);
            var destination = descendant ? Path.Combine(shared, "nested") : shared;
            Assert.Throws<IOException>(() => Subject.Move(_series, _source, destination));
            AssertContents(shared);
            new DirectoryInfo(_source).LinkTarget.Should().Be(shared);
        }

        [Test]
        public void should_not_replace_an_existing_folder_with_a_series_root_link()
        {
            var shared = Path.Combine(TempFolder, "shared-series");
            Directory.Move(_source, shared);
            Directory.CreateSymbolicLink(_source, shared);
            Directory.CreateDirectory(_destination);
            File.WriteAllText(Path.Combine(_destination, "foreign.txt"), "Foreign bytes");
            Assert.Throws<IOException>(() => Subject.Move(_series, _source, _destination));
            AssertContents(shared);
            File.ReadAllText(Path.Combine(_destination, "foreign.txt")).Should().Be("Foreign bytes");
        }

        [Test]
        public void should_preserve_both_distinct_case_variant_folders_on_move_failure()
        {
            _destination = Path.Combine(Path.GetDirectoryName(_source), "series");
            Directory.CreateDirectory(_destination);
            File.WriteAllText(Path.Combine(_destination, "foreign.txt"), "Other folder");
            if (File.Exists(Path.Combine(_source, "foreign.txt")))
            {
                Assert.Ignore("This scenario requires distinct case-sensitive directory entries.");
            }

            Assert.Throws<IOException>(() => Subject.Move(_series, _source, _destination));
            AssertContents(_source);
            File.ReadAllText(Path.Combine(_destination, "foreign.txt")).Should().Be("Other folder");
        }

        [TestCase("null")]
        [TestCase("wrong-series")]
        [TestCase("operation")]
        [TestCase("source-null")]
        [TestCase("destination-null")]
        [TestCase("relative-source")]
        [TestCase("relative-destination")]
        [TestCase("unrelated-current-path")]
        public void should_preserve_library_on_invalid_move_receipt(string corruption)
        {
            var data = JsonNode.Parse(Journal(true).ToJson()).AsObject();
            switch (corruption)
            {
                case "wrong-series": data["seriesId"] = 99; break;
                case "operation": data["operationId"] = "not-an-operation-id"; break;
                case "source-null": data["sourcePath"] = null; break;
                case "destination-null": data["destinationPath"] = null; break;
                case "relative-source": data["sourcePath"] = "relative/source"; break;
                case "relative-destination": data["destinationPath"] = "relative/destination"; break;
                case "unrelated-current-path":
                    _stored.Path = Path.Combine(TempFolder, "another-folder");
                    Db.Update(_stored);
                    break;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_journalPath));
            File.WriteAllText(_journalPath, corruption == "null" ? "null" : data.ToJsonString());
            Assert.Throws<IOException>(() => Subject.Recover(_series));
            AssertOriginalLibraryAndReceipt();
        }

        [TestCase("files")]
        [TestCase("directories")]
        [TestCase("links")]
        public void should_normalize_null_atomic_move_collections_without_changing_files(string collection)
        {
            var data = JsonNode.Parse(Journal(true).ToJson()).AsObject();
            data[collection] = null;
            Directory.CreateDirectory(Path.GetDirectoryName(_journalPath));
            File.WriteAllText(_journalPath, data.ToJsonString());
            Subject.Recover(_series);
            AssertContents(_source);
            File.Exists(_journalPath).Should().BeFalse();
        }

        [TestCase("original")]
        [TestCase("destination")]
        [TestCase("resolved")]
        public void should_reject_incomplete_link_intent_in_receipt(string missing)
        {
            var journal = Journal(true);
            var link = new SeriesFolderMoveService.MoveLink
            {
                RelativePath = Path.Combine("Season 1", "linked.mkv"),
                OriginalTarget = "original",
                DestinationTarget = "destination",
                ResolvedTarget = Path.Combine(TempFolder, "external")
            };
            if (missing == "original")
            {
                link.OriginalTarget = null;
            }
            else if (missing == "destination")
            {
                link.DestinationTarget = null;
            }
            else
            {
                link.ResolvedTarget = null;
            }

            journal.Links.Add(link);
            WriteJournal(journal);
            Assert.Throws<IOException>(() => Subject.Recover(_series));
            AssertOriginalLibraryAndReceipt();
        }

        [TestCase("kind")]
        [TestCase("original")]
        [TestCase("destination")]
        [TestCase("resolved")]
        public void should_reject_incomplete_series_root_link_intent(string missing)
        {
            var journal = Journal(true);
            journal.RootLink = new SeriesFolderMoveService.MoveLink
            {
                IsDirectory = missing != "kind",
                OriginalTarget = missing == "original" ? null : "original",
                DestinationTarget = missing == "destination" ? null : "destination",
                ResolvedTarget = missing == "resolved" ? null : Path.Combine(TempFolder, "external")
            };
            WriteJournal(journal);
            Assert.Throws<IOException>(() => Subject.Recover(_series));
            AssertOriginalLibraryAndReceipt();
        }

        [TestCase("null")]
        [TestCase("source")]
        [TestCase("destination")]
        [TestCase("identity")]
        public void should_not_claim_paths_when_pending_receipt_identity_is_invalid(string corruption)
        {
            var data = JsonNode.Parse(Journal(true).ToJson()).AsObject();
            if (corruption == "source")
            {
                data["sourcePath"] = null;
            }
            else if (corruption == "destination")
            {
                data["destinationPath"] = null;
            }
            else if (corruption == "identity")
            {
                data["seriesId"] = 99;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_journalPath));
            File.WriteAllText(_journalPath, corruption == "null" ? "null" : data.ToJsonString());
            Assert.Throws<IOException>(() => Subject.RecoverPending(Array.Empty<int>(), new[] { _destination }));
            AssertOriginalLibraryAndReceipt();
        }

        [Test]
        public void should_preserve_orphaned_move_receipt_when_series_was_removed()
        {
            WriteJournal(Journal(true));
            Db.Delete(_stored);
            Assert.Throws<IOException>(() => Subject.RecoverPending(Array.Empty<int>(), new[] { _destination }));
            AssertContents(_source);
            File.Exists(_journalPath).Should().BeTrue();
        }

        [Test]
        public void should_allow_path_operations_when_no_folder_recovery_directory_exists()
        {
            Subject.RecoverPending(new[] { _series.Id }, new[] { _destination });
            Subject.HasPendingMove(_series.Id).Should().BeFalse();
            WriteJournal(Journal(true));
            Subject.HasPendingMove(_series.Id).Should().BeTrue();
            Subject.RecoverPending(new[] { _series.Id }, Array.Empty<string>());
            Subject.HasPendingMove(_series.Id).Should().BeFalse();
            AssertContents(_source);
        }

        [Test]
        public void should_finish_recovery_when_another_recoverer_removes_receipt_while_waiting()
        {
            WriteJournal(Journal(true));
            using var observed = new ManualResetEventSlim();
            var inspected = false;
            Mocker.GetMock<IDiskProvider>().Setup(d => d.GetFileAttributes(_journalPath)).Returns<string>(path =>
            {
                var attributes = File.GetAttributes(path);
                inspected = true;
                return attributes;
            });
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FileExists(_journalPath)).Returns<string>(path =>
            {
                var exists = File.Exists(path);
                if (exists && inspected)
                {
                    observed.Set();
                }

                return exists;
            });
            var service = Subject;
            Task recovery;
            using (MediaFileOperationLock.AcquireAll())
            {
                recovery = Task.Run(() => service.Recover(_series));
                observed.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                File.Delete(_journalPath);
            }

            recovery.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            recovery.GetAwaiter().GetResult();
            AssertContents(_source);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_recover_case_rename_from_intermediate_folder(bool committed)
        {
            var journal = Journal(true);
            journal.CaseOnly = true;
            var stage = OperationStage(journal);
            Directory.CreateDirectory(Path.GetDirectoryName(stage));
            Directory.Move(_source, stage);
            if (committed)
            {
                _stored.Path = _destination;
                Db.Update(_stored);
            }

            WriteJournal(journal);
            Subject.Recover(_series);
            AssertContents(committed ? _destination : _source);
            Directory.Exists(stage).Should().BeFalse();
            File.Exists(_journalPath).Should().BeFalse();
        }

        [TestCase("source-and-stage")]
        [TestCase("source-and-destination")]
        [TestCase("missing")]
        [TestCase("committed-missing")]
        public void should_preserve_ambiguous_or_unavailable_atomic_move_state(string state)
        {
            var journal = Journal(true);
            var unavailable = Path.Combine(TempFolder, "unavailable");
            if (state == "source-and-stage")
            {
                Directory.CreateDirectory(OperationStage(journal));
                File.WriteAllText(Path.Combine(OperationStage(journal), "foreign.txt"), "Staging occupant");
            }
            else if (state == "source-and-destination")
            {
                Directory.CreateDirectory(_destination);
                File.WriteAllText(Path.Combine(_destination, "foreign.txt"), "Destination occupant");
            }
            else
            {
                Directory.Move(_source, unavailable);
                if (state == "committed-missing")
                {
                    _stored.Path = _destination;
                    Db.Update(_stored);
                }
            }

            WriteJournal(journal);
            Assert.Throws<IOException>(() => Subject.Recover(_series));
            AssertContents(state.Contains("missing") ? unavailable : _source);
            File.Exists(_journalPath).Should().BeTrue();
        }

        [Test]
        public void should_restore_both_distinct_case_folders_when_recovery_finds_a_conflict()
        {
            _destination = Path.Combine(Path.GetDirectoryName(_source), "series");
            Directory.CreateDirectory(_destination);
            File.WriteAllText(Path.Combine(_destination, "foreign.txt"), "Foreign bytes");
            if (File.Exists(Path.Combine(_source, "foreign.txt")))
            {
                Assert.Ignore("This scenario requires distinct case-sensitive directory entries.");
            }

            var journal = Journal(true);
            journal.CaseOnly = true;
            WriteJournal(journal);
            Assert.Throws<IOException>(() => Subject.Recover(_series));
            AssertContents(_source);
            File.ReadAllText(Path.Combine(_destination, "foreign.txt")).Should().Be("Foreign bytes");
            File.Exists(_journalPath).Should().BeTrue();
        }

        [Test]
        public void should_handle_case_aliases_during_recovery_without_losing_directory_contents()
        {
            _destination = Path.Combine(Path.GetDirectoryName(_source), "series");
            var journal = Journal(true);
            journal.CaseOnly = true;
            WriteJournal(journal);
            string Resolve(string path) => path == _destination ? _source : path;
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FolderExists(It.IsAny<string>())).Returns<string>(path => Directory.Exists(Resolve(path)));
            Mocker.GetMock<IDiskProvider>().Setup(d => d.GetFileAttributes(It.IsAny<string>())).Returns<string>(path => File.GetAttributes(Resolve(path)));
            Mocker.GetMock<IDiskProvider>().Setup(d => d.MoveFolder(It.IsAny<string>(), It.IsAny<string>())).Callback<string, string>((source, target) => Directory.Move(Resolve(source), target));

            Subject.Recover(_series);

            AssertContents(_source);
            File.Exists(_journalPath).Should().BeFalse();
        }

        [Test]
        public void should_treat_committed_move_as_success_when_notification_fails_once()
        {
            Mocker.GetMock<NzbDrone.Core.Messaging.Events.IEventAggregator>()
                .Setup(e => e.PublishEvent(It.IsAny<SeriesEditedEvent>())).Throws(new IOException("Notification failed"));
            Subject.Move(_series, _source, _destination);
            AssertContents(_destination);
            Db.All<Series>().Single().Path.Should().Be(_destination);
            File.Exists(_journalPath).Should().BeFalse();
        }

        private (SeriesFolderMoveService.MoveJournal Journal, string Shared) RootCopyReceipt(bool committed, bool prepared)
        {
            var shared = Path.Combine(TempFolder, "shared-root");
            Directory.Move(_source, shared);
            Directory.CreateSymbolicLink(_source, shared);
            var journal = Journal(true);
            journal.AtomicMove = false;
            journal.Prepared = prepared;
            journal.RootLink = new SeriesFolderMoveService.MoveLink
            {
                IsDirectory = true,
                OriginalTarget = shared,
                DestinationTarget = shared,
                ResolvedTarget = shared
            };
            if (committed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_destination));
                Directory.CreateSymbolicLink(_destination, shared);
                _stored.Path = _destination;
                Db.Update(_stored);
            }

            WriteJournal(journal);
            return (journal, shared);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_discard_only_its_staged_root_link_after_interrupted_copy(bool prepared)
        {
            var receipt = RootCopyReceipt(false, prepared);
            var stage = OperationStage(receipt.Journal);
            Directory.CreateDirectory(Path.GetDirectoryName(stage));
            Directory.CreateSymbolicLink(stage, receipt.Shared);
            Subject.Recover(_series);
            new DirectoryInfo(_source).LinkTarget.Should().Be(receipt.Shared);
            new DirectoryInfo(stage).LinkTarget.Should().BeNull();
            AssertContents(receipt.Shared);
            File.Exists(_journalPath).Should().BeFalse();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_preserve_replacements_of_the_original_root_link_after_commit(bool file)
        {
            var receipt = RootCopyReceipt(true, true);
            Directory.Delete(_source);
            var marker = _source;
            if (!file)
            {
                Directory.CreateDirectory(_source);
                marker = Path.Combine(_source, "foreign.txt");
            }

            File.WriteAllText(marker, "Foreign replacement");
            Assert.Throws<IOException>(() => Subject.Recover(_series));
            File.ReadAllText(marker).Should().Be("Foreign replacement");
            AssertContents(receipt.Shared);
            File.Exists(_journalPath).Should().BeTrue();
        }

        [Test]
        public void should_finish_root_link_cleanup_when_source_link_was_already_removed()
        {
            var receipt = RootCopyReceipt(true, true);
            Directory.Delete(_source);
            Subject.Recover(_series);
            AssertContents(receipt.Shared);
            new DirectoryInfo(_destination).LinkTarget.Should().Be(receipt.Shared);
            File.Exists(_journalPath).Should().BeFalse();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_preserve_foreign_entries_at_root_link_staging_path(bool file)
        {
            var receipt = RootCopyReceipt(false, true);
            var stage = OperationStage(receipt.Journal);
            Directory.CreateDirectory(Path.GetDirectoryName(stage));
            var marker = stage;
            if (!file)
            {
                Directory.CreateDirectory(stage);
                marker = Path.Combine(stage, "foreign.txt");
            }

            File.WriteAllText(marker, "Foreign staging entry");
            Assert.Throws<IOException>(() => Subject.Recover(_series));
            File.ReadAllText(marker).Should().Be("Foreign staging entry");
            AssertContents(receipt.Shared);
            File.Exists(_journalPath).Should().BeTrue();
        }

        [Test]
        public void should_preserve_published_copy_when_original_storage_is_unavailable()
        {
            var journal = Journal(false, true);
            var stage = Stage(journal);
            Directory.Move(stage, _destination);
            var unavailable = Path.Combine(TempFolder, "unavailable");
            Directory.Move(_source, unavailable);
            WriteJournal(journal);
            Assert.Throws<IOException>(() => Subject.Recover(_series));
            AssertContents(unavailable);
            AssertContents(_destination);
            File.Exists(_journalPath).Should().BeTrue();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_keep_source_when_committed_destination_file_is_missing_or_changed(bool missing)
        {
            var journal = Journal(false, true);
            var stage = Stage(journal);
            Directory.Move(stage, _destination);
            WriteJournal(journal);
            _stored.Path = _destination;
            Db.Update(_stored);
            var target = Path.Combine(_destination, journal.Files[0].RelativePath);
            if (missing)
            {
                File.Delete(target);
            }
            else
            {
                File.WriteAllText(target, "Changed destination bytes");
            }

            Assert.Throws<IOException>(() => Subject.Recover(_series));
            AssertContents(_source);
            File.Exists(_journalPath).Should().BeTrue();
        }

        [TestCase("file")]
        [TestCase("directory")]
        [TestCase("link")]
        public void should_keep_unexpected_staging_entries_for_review(string entryType)
        {
            var journal = Journal(false);
            var stage = Stage(journal);
            var foreign = Path.Combine(stage, "unexpected");
            if (entryType == "directory")
            {
                Directory.CreateDirectory(foreign);
            }
            else if (entryType == "link")
            {
                File.CreateSymbolicLink(foreign, Path.Combine(_source, "poster.jpg"));
            }
            else
            {
                File.WriteAllText(foreign, "Foreign staging bytes");
            }

            WriteJournal(journal);
            Assert.Throws<IOException>(() => Subject.Recover(_series));
            AssertOriginalLibraryAndReceipt();
            (File.Exists(foreign) || Directory.Exists(foreign)).Should().BeTrue();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_preserve_staging_occupants_created_before_copy_starts(bool file)
        {
            RequireCopy();
            string marker = null;
            Mocker.GetMock<IDiskProvider>().Setup(d => d.WriteAllText(It.IsAny<string>(), It.IsAny<string>()))
                .Callback<string, string>((path, contents) =>
                {
                    File.WriteAllText(path, contents);
                    var journal = Json.Deserialize<SeriesFolderMoveService.MoveJournal>(contents);
                    if (!journal.AtomicMove && marker == null)
                    {
                        var stage = OperationStage(journal);
                        if (file)
                        {
                            marker = stage;
                        }
                        else
                        {
                            Directory.CreateDirectory(stage);
                            marker = Path.Combine(stage, "foreign.txt");
                        }

                        File.WriteAllText(marker, "Foreign staging occupant");
                    }
                });
            Assert.Throws<IOException>(() => Subject.Move(_series, _source, _destination));
            AssertContents(_source);
            File.ReadAllText(marker).Should().Be("Foreign staging occupant");
        }

        [Test]
        public void should_preserve_files_arriving_during_source_cleanup()
        {
            RequireCopy();
            var late = Path.Combine(_source, "arrived-during-cleanup.txt");
            var sourceFiles = _contents.Keys.Select(path => Path.Combine(_source, path)).ToHashSet();
            Mocker.GetMock<IDiskProvider>().Setup(d => d.DeleteFile(It.IsAny<string>())).Callback<string>(path =>
            {
                File.Delete(path);
                if (sourceFiles.Contains(path) && sourceFiles.All(file => !File.Exists(file)))
                {
                    File.WriteAllText(late, "Late source bytes");
                }
            });
            Assert.Throws<IOException>(() => Subject.Move(_series, _source, _destination));
            File.ReadAllText(late).Should().Be("Late source bytes");
            AssertContents(_destination);
            File.Exists(_journalPath).Should().BeTrue();
        }

        [Test]
        public void should_preserve_files_arriving_during_staging_cleanup()
        {
            Directory.CreateDirectory(_destination);
            string stage = null;
            string late = null;
            var inserted = false;
            Mocker.GetMock<ISeriesRepository>().Setup(r => r.UpdatePath(It.IsAny<int>(), It.IsAny<string>()))
                .Returns<int, string>((_, _) =>
                {
                    stage = OperationStage(Json.Deserialize<SeriesFolderMoveService.MoveJournal>(File.ReadAllText(_journalPath)));
                    late = Path.Combine(stage, "arrived-during-cleanup.txt");
                    throw new IOException("Database unavailable");
                });
            Mocker.GetMock<IDiskProvider>().Setup(d => d.GetFiles(It.IsAny<string>(), It.IsAny<bool>()))
                .Returns<string, bool>((path, recursive) =>
                {
                    var files = Directory.GetFiles(path, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
                    if (path == stage && !recursive && !inserted)
                    {
                        File.WriteAllText(late, "Late staging bytes");
                        inserted = true;
                    }

                    return files;
                });
            Assert.Throws<IOException>(() => Subject.Move(_series, _source, _destination));
            File.ReadAllText(late).Should().Be("Late staging bytes");
            AssertOriginalLibraryAndReceipt();
        }

        [Test]
        public void should_reject_case_conflicting_source_entries_when_copying()
        {
            RequireCopy();
            var other = Path.Combine(_source, "POSTER.JPG");
            File.WriteAllText(other, "Distinct artwork");
            if (File.ReadAllText(Path.Combine(_source, "poster.jpg")) == "Distinct artwork")
            {
                Assert.Ignore("This scenario requires case-sensitive source filenames.");
            }

            Assert.Throws<IOException>(() => Subject.Move(_series, _source, _destination));
            File.ReadAllText(other).Should().Be("Distinct artwork");
            AssertContents(_source);
        }

        [TestCase("directory-for-file")]
        [TestCase("file-for-directory")]
        [TestCase("case-directory")]
        public void should_reject_namespace_collisions_while_merging_folders(string collision)
        {
            Directory.CreateDirectory(_destination);
            if (collision == "directory-for-file")
            {
                Directory.CreateDirectory(Path.Combine(_destination, "poster.jpg"));
            }
            else if (collision == "file-for-directory")
            {
                File.WriteAllText(Path.Combine(_destination, "Season 1"), "Foreign file");
            }
            else
            {
                Directory.CreateDirectory(Path.Combine(_destination, "season 1"));
            }

            Assert.Throws<IOException>(() => Subject.Move(_series, _source, _destination));
            AssertContents(_source);
        }

        [Test]
        public void should_merge_nested_links_without_copying_their_target_contents()
        {
            Directory.CreateDirectory(_destination);
            File.WriteAllText(Path.Combine(_destination, "foreign.txt"), "Destination bytes");
            var shared = Path.Combine(TempFolder, "shared");
            Directory.CreateDirectory(shared);
            File.WriteAllText(Path.Combine(shared, "external.mkv"), "External bytes");
            Directory.CreateSymbolicLink(Path.Combine(_source, "Season 1", "shared-link"), shared);
            File.CreateSymbolicLink(Path.Combine(_source, "Season 1", "file-link.mkv"), Path.Combine(shared, "external.mkv"));
            Directory.CreateSymbolicLink(Path.Combine(_source, "Season 1", "root-link"), "..");

            Subject.Move(_series, _source, _destination);

            File.ReadAllText(Path.Combine(shared, "external.mkv")).Should().Be("External bytes");
            new DirectoryInfo(Path.Combine(_destination, "Season 1", "root-link")).ResolveLinkTarget(true).FullName.Should().Be(_destination);
            new DirectoryInfo(Path.Combine(_destination, "Season 1", "shared-link")).ResolveLinkTarget(true).FullName.Should().Be(shared);
            File.ReadAllText(Path.Combine(_destination, "Season 1", "file-link.mkv")).Should().Be("External bytes");
        }

        [Test]
        public void should_rollback_published_links_but_preserve_unpublished_links_and_external_bytes()
        {
            var journal = Journal(false, true, true);
            var stage = Stage(journal);
            var shared = Path.Combine(TempFolder, "shared");
            Directory.CreateDirectory(shared);
            File.WriteAllText(Path.Combine(shared, "external.mkv"), "External bytes");
            Directory.CreateDirectory(_destination);
            foreach (var name in new[] { "published", "staged", "missing" })
            {
                var link = new SeriesFolderMoveService.MoveLink
                {
                    RelativePath = name,
                    OriginalTarget = shared,
                    DestinationTarget = shared,
                    ResolvedTarget = shared,
                    IsDirectory = true
                };
                journal.Links.Add(link);
                Directory.CreateSymbolicLink(Path.Combine(_source, name), shared);
                if (name != "missing")
                {
                    Directory.CreateSymbolicLink(Path.Combine(name == "published" ? _destination : stage, name), shared);
                }
            }

            WriteJournal(journal);
            Subject.Recover(_series);
            new DirectoryInfo(Path.Combine(_destination, "published")).LinkTarget.Should().BeNull();
            File.ReadAllText(Path.Combine(shared, "external.mkv")).Should().Be("External bytes");
            AssertContents(_source);
            File.Exists(_journalPath).Should().BeFalse();
        }

        private (SeriesFolderMoveService.MoveJournal Journal, string Path, string Backup, string Temporary) PendingFileLink()
        {
            var journal = Journal(true);
            var relative = Path.Combine("Season 1", "linked.mkv");
            var link = new SeriesFolderMoveService.MoveLink
            {
                RelativePath = relative,
                OriginalTarget = Path.Combine(_source, "poster.jpg"),
                DestinationTarget = Path.Combine(_destination, "poster.jpg"),
                ResolvedTarget = Path.Combine(_source, "poster.jpg")
            };
            journal.Links.Add(link);
            var path = Path.Combine(_source, relative);
            File.CreateSymbolicLink(path, link.OriginalTarget);
            WriteJournal(journal);
            return (journal, path, path + ".sonarr-link-backup-" + journal.OperationId, path + ".sonarr-link-new-" + journal.OperationId);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_not_restore_link_backup_over_a_foreign_entry(bool directory)
        {
            var pending = PendingFileLink();
            File.Move(pending.Path, pending.Backup);
            var marker = pending.Path;
            if (directory)
            {
                Directory.CreateDirectory(pending.Path);
                marker = Path.Combine(pending.Path, "foreign.txt");
            }

            File.WriteAllText(marker, "Foreign replacement");
            Assert.Throws<IOException>(() => Subject.Recover(_series));
            File.ReadAllText(marker).Should().Be("Foreign replacement");
            new FileInfo(pending.Backup).LinkTarget.Should().Be(pending.Journal.Links[0].OriginalTarget);
            AssertOriginalLibraryAndReceipt();
        }

        [TestCase("file")]
        [TestCase("directory")]
        [TestCase("link")]
        public void should_preserve_replaced_link_before_recovery_rewrites_it(string entryType)
        {
            var pending = PendingFileLink();
            File.Delete(pending.Path);
            var marker = pending.Path;
            if (entryType == "directory")
            {
                Directory.CreateDirectory(pending.Path);
                marker = Path.Combine(pending.Path, "foreign.txt");
            }
            else if (entryType == "link")
            {
                marker = Path.Combine(TempFolder, "foreign-target");
                File.WriteAllText(marker, "Foreign replacement");
                File.CreateSymbolicLink(pending.Path, marker);
            }

            if (entryType != "link")
            {
                File.WriteAllText(marker, "Foreign replacement");
            }

            Assert.Throws<IOException>(() => Subject.Recover(_series));
            File.ReadAllText(marker).Should().Be("Foreign replacement");
            AssertOriginalLibraryAndReceipt();
        }

        [Test]
        public void should_resume_a_prepared_link_replacement_without_recreating_it()
        {
            var pending = PendingFileLink();
            File.Delete(pending.Path);
            File.CreateSymbolicLink(pending.Path, pending.Journal.Links[0].DestinationTarget);
            File.CreateSymbolicLink(pending.Temporary, pending.Journal.Links[0].OriginalTarget);
            Subject.Recover(_series);
            new FileInfo(pending.Path).LinkTarget.Should().Be(pending.Journal.Links[0].OriginalTarget);
            new FileInfo(pending.Backup).LinkTarget.Should().BeNull();
            new FileInfo(pending.Temporary).LinkTarget.Should().BeNull();
            File.Exists(_journalPath).Should().BeFalse();
        }

        [TestCase("file")]
        [TestCase("directory")]
        [TestCase("link")]
        public void should_not_overwrite_an_occupied_link_backup_path(string entryType)
        {
            var pending = PendingFileLink();
            File.Delete(pending.Path);
            File.CreateSymbolicLink(pending.Path, pending.Journal.Links[0].DestinationTarget);
            var marker = pending.Backup;
            if (entryType == "directory")
            {
                Directory.CreateDirectory(marker);
                marker = Path.Combine(marker, "foreign.txt");
            }
            else if (entryType == "link")
            {
                marker = Path.Combine(TempFolder, "foreign-backup-target");
                File.WriteAllText(marker, "Foreign backup");
                File.CreateSymbolicLink(pending.Backup, marker);
            }

            if (entryType != "link")
            {
                File.WriteAllText(marker, "Foreign backup");
            }

            Assert.Throws<IOException>(() => Subject.Recover(_series));
            File.ReadAllText(marker).Should().Be("Foreign backup");
            AssertOriginalLibraryAndReceipt();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_preserve_changed_link_recovery_artifacts(bool temporary)
        {
            var pending = PendingFileLink();
            var foreign = Path.Combine(TempFolder, "foreign-artifact-target");
            File.WriteAllText(foreign, "Foreign bytes");
            File.CreateSymbolicLink(temporary ? pending.Temporary : pending.Backup, foreign);
            Assert.Throws<IOException>(() => Subject.Recover(_series));
            File.ReadAllText(foreign).Should().Be("Foreign bytes");
            AssertOriginalLibraryAndReceipt();
        }

        [Test]
        public void should_not_move_a_link_backup_replaced_after_inspection()
        {
            var pending = PendingFileLink();
            File.Move(pending.Path, pending.Backup);
            var replaced = false;
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FolderExists(pending.Path)).Returns(() =>
            {
                if (!replaced)
                {
                    File.Delete(pending.Backup);
                    File.WriteAllText(pending.Backup, "Replaced after inspection");
                    replaced = true;
                }

                return false;
            });
            Assert.Throws<IOException>(() => Subject.Recover(_series));
            File.ReadAllText(pending.Backup).Should().Be("Replaced after inspection");
            File.Exists(pending.Path).Should().BeFalse();
            AssertOriginalLibraryAndReceipt();
        }

        [Test]
        public void should_detect_changed_indirect_link_target_during_copy()
        {
            RequireCopy();
            var first = Path.Combine(TempFolder, "external-one");
            var second = Path.Combine(TempFolder, "external-two");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            File.WriteAllText(Path.Combine(first, "external.mkv"), "First bytes");
            File.WriteAllText(Path.Combine(second, "external.mkv"), "Second bytes");
            var alias = Path.Combine(TempFolder, "external-alias");
            Directory.CreateSymbolicLink(alias, first);
            Directory.CreateSymbolicLink(Path.Combine(_source, "linked-season"), alias);
            var swapped = false;
            Mocker.GetMock<IDiskTransferService>().Setup(d => d.TransferFile(It.IsAny<string>(), It.IsAny<string>(), TransferMode.Copy, false))
                .Returns<string, string, TransferMode, bool>((source, destination, mode, overwrite) =>
                {
                    File.Copy(source, destination, overwrite);
                    if (!swapped)
                    {
                        Directory.Delete(alias);
                        Directory.CreateSymbolicLink(alias, second);
                        swapped = true;
                    }

                    return mode;
                });
            Assert.Throws<IOException>(() => Subject.Move(_series, _source, _destination));
            File.ReadAllText(Path.Combine(first, "external.mkv")).Should().Be("First bytes");
            File.ReadAllText(Path.Combine(second, "external.mkv")).Should().Be("Second bytes");
            AssertContents(_source);
        }

        [Test]
        public void should_not_delete_alias_retargeted_files_after_verifying_original_source_stream()
        {
            var originalRoot = _source;
            var alias = Path.Combine(TempFolder, "source-alias");
            Directory.CreateSymbolicLink(alias, Path.GetDirectoryName(originalRoot));
            var victimParent = Path.Combine(TempFolder, "unrelated-storage");
            var victimRoot = Path.Combine(victimParent, "Series");
            var victims = new Dictionary<string, string>();
            foreach (var file in _contents)
            {
                var path = Path.Combine(victimRoot, file.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                victims[path] = "Unrelated " + file.Value;
                File.WriteAllText(path, victims[path]);
            }

            _source = Path.Combine(alias, "Series");
            _series.Path = _source;
            _stored.Path = _source;
            Db.Update(_stored);
            RequireCopy();
            var verificationReads = 0;
            var swapped = false;
            Mocker.GetMock<IDiskProvider>().Setup(d => d.OpenReadStream(It.IsAny<string>())).Returns<string>(path =>
            {
                var stream = File.OpenRead(path);
                if (_stored.Path == _destination &&
                    (path == Path.Combine(_source, "poster.jpg") || path == Path.Combine(originalRoot, "poster.jpg")) &&
                    ++verificationReads == 1)
                {
                    Directory.Delete(alias);
                    Directory.CreateSymbolicLink(alias, victimParent);
                    swapped = true;
                }

                return stream;
            });

            try
            {
                Subject.Move(_series, _source, _destination);
            }
            catch (IOException)
            {
                // Mapping changes may stop recovery; they must never redirect cleanup to the new target.
            }

            swapped.Should().BeTrue();
            foreach (var victim in victims)
            {
                File.ReadAllText(victim.Key).Should().Be(victim.Value);
            }

            AssertContents(_destination);
        }

        [Test]
        public void should_update_namespace_without_moving_bytes_when_paths_resolve_to_same_folder()
        {
            var alias = Path.Combine(TempFolder, "same-folder-alias");
            Directory.CreateSymbolicLink(alias, Path.GetDirectoryName(_source));
            var destination = Path.Combine(alias, "Series");
            Subject.Move(_series, _source, destination);
            Db.All<Series>().Single().Path.Should().Be(destination);
            AssertContents(_source);
            Mocker.GetMock<IDiskProvider>().Verify(d => d.MoveFolder(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_resolve_relative_external_links_against_physical_destination_parent(bool copy)
        {
            var physicalParent = Path.Combine(TempFolder, "physical-destination");
            Directory.CreateDirectory(physicalParent);
            var alias = Path.Combine(TempFolder, "logical", "nested", "destination-alias");
            Directory.CreateDirectory(Path.GetDirectoryName(alias));
            Directory.CreateSymbolicLink(alias, physicalParent);
            _destination = Path.Combine(alias, "Series");
            var external = Path.Combine(TempFolder, "external-season");
            Directory.CreateDirectory(external);
            File.WriteAllText(Path.Combine(external, "external.mkv"), "Shared bytes");
            Directory.CreateSymbolicLink(Path.Combine(_source, "Season 3"), Path.GetRelativePath(_source, external));
            if (copy)
            {
                RequireCopy();
            }

            Subject.Move(_series, _source, _destination);

            File.ReadAllText(Path.Combine(_destination, "Season 3", "external.mkv")).Should().Be("Shared bytes");
            File.ReadAllText(Path.Combine(external, "external.mkv")).Should().Be("Shared bytes");
            AssertContents(Path.Combine(physicalParent, "Series"));
        }
    }
}
