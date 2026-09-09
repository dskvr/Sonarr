using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Subtitles;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Extras.Subtitles
{
    [TestFixture]
    public class SubtitleRenameRecoveryFixture : DbTest<SubtitleService, SubtitleFile>
    {
        private Series _series;
        private EpisodeFile _episodeFile;
        private string _source;
        private string _destination;
        private bool _interruptAfterMove;
        private bool _interruptReceiptClear;

        [SetUp]
        public void Setup()
        {
            _interruptAfterMove = false;
            _interruptReceiptClear = false;
            Directory.CreateDirectory(TempFolder);
            _series = new Series { Id = 1, Path = TempFolder };
            _episodeFile = new EpisodeFile { Id = 10, SeriesId = 1, RelativePath = "New.mkv" };
            _source = Path.Combine(TempFolder, "Old.en.srt");
            _destination = Path.Combine(TempFolder, "New.en.srt");
            File.WriteAllText(_source, "original subtitle content");
            Db.Insert(new SubtitleFile
            {
                SeriesId = 1, SeasonNumber = 1, EpisodeFileId = 10, RelativePath = "Old.en.srt", Extension = ".srt", Language = Language.English
            });
            Mocker.SetConstant<IExtraFileRepository<SubtitleFile>>(Mocker.Resolve<ExtraFileRepository<SubtitleFile>>());
            Mocker.SetConstant<ISubtitleFileService>(Mocker.Resolve<SubtitleFileService>());
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FileExists(It.IsAny<string>())).Returns((string path) => File.Exists(path));
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FolderExists(It.IsAny<string>())).Returns((string path) => Directory.Exists(path));
            Mocker.GetMock<IDiskProvider>().Setup(d => d.CreateFolder(It.IsAny<string>())).Callback<string>(path => Directory.CreateDirectory(path));
            Mocker.GetMock<IDiskProvider>().Setup(d => d.GetFileAttributes(It.IsAny<string>())).Returns((string path) => File.GetAttributes(path));
            Mocker.GetMock<IDiskProvider>().Setup(d => d.OpenReadStream(It.IsAny<string>())).Returns((string path) => File.OpenRead(path));
            Mocker.GetMock<IDiskProvider>().Setup(d => d.WriteAllText(It.IsAny<string>(), It.IsAny<string>())).Callback<string, string>(File.WriteAllText);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.ReadAllText(It.IsAny<string>())).Returns((string path) => File.ReadAllText(path));
            Mocker.GetMock<IDiskProvider>().Setup(d => d.DeleteFile(It.IsAny<string>())).Callback<string>(path =>
            {
                if (_interruptReceiptClear && path.EndsWith(".json"))
                {
                    throw new IOException("Interrupted after database commit");
                }

                File.Delete(path);
            });
            Mocker.GetMock<IDiskProvider>().Setup(d => d.MoveFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
                .Callback<string, string, bool>((source, destination, overwrite) =>
                {
                    File.Move(source, destination, overwrite);
                    if (_interruptAfterMove && source == _source)
                    {
                        throw new IOException("Interrupted after physical move, before database commit");
                    }
                });
        }

        private string[] Receipts() => Directory.GetFiles(Path.Combine(TestFolderInfo.AppDataFolder, "MediaFileRecovery", "extras"), "*.json");

        [Test]
        public void should_recover_real_database_ownership_at_move_before_commit_cutpoint()
        {
            _interruptAfterMove = true;
            var id = StoredModel.Id;
            Action rename = () => Subject.MoveFilesAfterRename(_series, new List<EpisodeFile> { _episodeFile }, true);

            rename.Should().Throw<AggregateException>();

            StoredModel.RelativePath.Should().Be("Old.en.srt");
            File.Exists(_source).Should().BeFalse();
            File.ReadAllText(_destination).Should().Be("original subtitle content");
            Receipts().Should().ContainSingle();
            _interruptAfterMove = false;

            Mocker.Resolve<SubtitleService>().MoveFilesAfterRename(_series, new List<EpisodeFile> { _episodeFile }, true);

            StoredModel.Id.Should().Be(id);
            StoredModel.RelativePath.Should().Be("New.en.srt");
            StoredModel.EpisodeFileId.Should().Be(10);
            File.ReadAllText(_destination).Should().Be("original subtitle content");
            Receipts().Should().BeEmpty();
        }

        [Test]
        [Platform("Linux")]
        public void should_recover_real_database_after_transfer_process_dies_before_commit()
        {
            Mocker.GetMock<IDiskProvider>().Setup(d => d.MoveFile(_source, _destination, false)).Callback(() =>
            {
                var startInfo = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add("if mv -- \"$1\" \"$2\"; then kill -KILL $$; fi");
                startInfo.ArgumentList.Add("sidecar-transfer");
                startInfo.ArgumentList.Add(_source);
                startInfo.ArgumentList.Add(_destination);
                using var process = Process.Start(startInfo);
                if (!process.WaitForExit(10000))
                {
                    process.Kill();
                    throw new IOException("Sidecar transfer process did not terminate");
                }

                process.ExitCode.Should().NotBe(0);
                throw new IOException("Sidecar transfer process terminated before reporting completion");
            });
            Action rename = () => Subject.MoveFilesAfterRename(_series, new List<EpisodeFile> { _episodeFile }, true);

            rename.Should().Throw<AggregateException>();

            StoredModel.RelativePath.Should().Be("Old.en.srt");
            File.Exists(_source).Should().BeFalse();
            File.ReadAllText(_destination).Should().Be("original subtitle content");
            Receipts().Should().ContainSingle();

            Mocker.Resolve<SubtitleService>().MoveFilesAfterRename(_series, new List<EpisodeFile> { _episodeFile }, true);

            StoredModel.RelativePath.Should().Be("New.en.srt");
            StoredModel.EpisodeFileId.Should().Be(10);
            File.ReadAllText(_destination).Should().Be("original subtitle content");
            Receipts().Should().BeEmpty();
        }

        [Test]
        public void should_finish_interrupted_copy_before_source_unlink_without_losing_original_bytes()
        {
            Mocker.GetMock<IDiskProvider>().Setup(d => d.MoveFile(_source, _destination, false)).Callback(() =>
            {
                File.Copy(_source, _destination);
                throw new IOException("Interrupted cross-volume move before source unlink");
            });
            Action rename = () => Subject.MoveFilesAfterRename(_series, new List<EpisodeFile> { _episodeFile }, true);
            rename.Should().Throw<AggregateException>();
            File.ReadAllText(_source).Should().Be("original subtitle content");
            StoredModel.RelativePath.Should().Be("Old.en.srt");

            Mocker.Resolve<SubtitleService>().MoveFilesAfterRename(_series, new List<EpisodeFile> { _episodeFile }, true);

            File.Exists(_source).Should().BeFalse();
            File.ReadAllText(_destination).Should().Be("original subtitle content");
            StoredModel.RelativePath.Should().Be("New.en.srt");
            Receipts().Should().BeEmpty();
        }

        [TestCase("null")]
        [TestCase("SeriesId")]
        [TestCase("EpisodeFileId")]
        [TestCase("ExtraFileId")]
        [TestCase("ExtraFileType")]
        [TestCase("DestinationPath")]
        [TestCase("SourcePath")]
        public void should_refuse_mismatched_receipt_without_changing_database_or_moved_bytes(string field)
        {
            _interruptAfterMove = true;
            Action rename = () => Subject.MoveFilesAfterRename(_series, new List<EpisodeFile> { _episodeFile }, true);
            rename.Should().Throw<AggregateException>();
            var path = Receipts().Single();
            if (field == "null")
            {
                File.WriteAllText(path, "null");
            }
            else
            {
                var receipt = JsonNode.Parse(File.ReadAllText(path)).AsObject();
                var key = receipt.Select(p => p.Key).Single(k => k.Equals(field, StringComparison.OrdinalIgnoreCase));
                receipt[key] = field.EndsWith("Id") ? JsonValue.Create(999) : JsonValue.Create(Path.Combine(TempFolder, "wrong-owner-or-path"));
                File.WriteAllText(path, receipt.ToJsonString());
            }

            rename.Should().Throw<AggregateException>();

            StoredModel.RelativePath.Should().Be("Old.en.srt");
            File.ReadAllText(_destination).Should().Be("original subtitle content");
            Receipts().Should().ContainSingle();
        }

        [Test]
        [Platform("Linux")]
        public void should_not_follow_a_link_substituted_for_regular_recovery_destination()
        {
            _interruptAfterMove = true;
            Action rename = () => Subject.MoveFilesAfterRename(_series, new List<EpisodeFile> { _episodeFile }, true);
            rename.Should().Throw<AggregateException>();
            File.Delete(_destination);
            var external = Path.Combine(TempFolder, "unrelated.srt");
            File.WriteAllText(external, "unrelated bytes");
            File.CreateSymbolicLink(_destination, external);

            rename.Should().Throw<AggregateException>();

            StoredModel.RelativePath.Should().Be("Old.en.srt");
            File.ReadAllText(external).Should().Be("unrelated bytes");
            new FileInfo(_destination).LinkTarget.Should().Be(external);
            Receipts().Should().ContainSingle();
            Mocker.GetMock<IDiskProvider>().Verify(d => d.OpenReadStream(external), Times.Never());
        }

        [TestCase("OriginalLinkTarget")]
        [TestCase("DestinationLinkTarget")]
        [TestCase("empty")]
        [Platform("Linux")]
        public void should_refuse_changed_link_intent_after_commit_before_receipt_clear(string field)
        {
            var external = Path.Combine(TempFolder, "external.srt");
            File.WriteAllText(external, "external bytes");
            File.Delete(_source);
            File.CreateSymbolicLink(_source, external);
            _interruptReceiptClear = true;
            Action rename = () => Subject.MoveFilesAfterRename(_series, new List<EpisodeFile> { _episodeFile }, true);
            rename.Should().Throw<AggregateException>();
            var path = Receipts().Single();
            var receipt = JsonNode.Parse(File.ReadAllText(path)).AsObject();
            var key = receipt.Select(p => p.Key).Single(k => k.Equals(field == "empty" ? "DestinationLinkTarget" : field, StringComparison.OrdinalIgnoreCase));
            receipt[key] = field == "empty" ? "" : "another-target.srt";
            File.WriteAllText(path, receipt.ToJsonString());
            _interruptReceiptClear = false;

            rename.Should().Throw<AggregateException>();

            StoredModel.RelativePath.Should().Be("New.en.srt");
            new FileInfo(_destination).LinkTarget.Should().Be(external);
            File.ReadAllText(external).Should().Be("external bytes");
            Receipts().Should().ContainSingle();
        }

        [TestCase(false)]
        [TestCase(true)]
        [Platform("Linux")]
        public void should_clean_only_matching_staged_links_when_recovering_committed_rename(bool foreignTemporaryLink)
        {
            var external = Path.Combine(TempFolder, "external.srt");
            File.WriteAllText(external, "external bytes");
            File.Delete(_source);
            File.CreateSymbolicLink(_source, external);
            _interruptReceiptClear = true;
            Action rename = () => Subject.MoveFilesAfterRename(_series, new List<EpisodeFile> { _episodeFile }, true);
            rename.Should().Throw<AggregateException>();
            var receipt = JsonNode.Parse(File.ReadAllText(Receipts().Single())).AsObject();
            var temporaryPath = receipt.Single(p => p.Key.Equals("TemporaryLinkPath", StringComparison.OrdinalIgnoreCase)).Value.GetValue<string>();
            File.CreateSymbolicLink(temporaryPath, foreignTemporaryLink ? "different-target.srt" : external);
            _interruptReceiptClear = false;

            if (foreignTemporaryLink)
            {
                rename.Should().Throw<AggregateException>();
                new FileInfo(temporaryPath).LinkTarget.Should().Be("different-target.srt");
                Receipts().Should().ContainSingle();
            }
            else
            {
                rename.Should().NotThrow();
                new FileInfo(temporaryPath).LinkTarget.Should().BeNull();
                Receipts().Should().BeEmpty();
            }

            File.ReadAllText(external).Should().Be("external bytes");
            StoredModel.RelativePath.Should().Be("New.en.srt");
            new FileInfo(_destination).LinkTarget.Should().Be(external);
        }

        [Test]
        public void should_finish_receipt_after_database_committed_before_process_stopped()
        {
            _interruptReceiptClear = true;
            Action rename = () => Subject.MoveFilesAfterRename(_series, new List<EpisodeFile> { _episodeFile }, true);
            rename.Should().Throw<AggregateException>();
            StoredModel.RelativePath.Should().Be("New.en.srt");
            Receipts().Should().ContainSingle();
            _interruptReceiptClear = false;

            rename.Should().NotThrow();

            AllStoredModels.Should().ContainSingle();
            Receipts().Should().BeEmpty();
            File.ReadAllText(_destination).Should().Be("original subtitle content");
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        [Platform("Linux")]
        public void should_preserve_external_symlink_target_across_rename_and_commit_failure(bool changeDirectory, bool failCommit)
        {
            File.Delete(_source);
            _series.Path = Path.Combine(TempFolder, "series");
            Directory.CreateDirectory(_series.Path);
            var externalDirectory = Path.Combine(TempFolder, "external");
            Directory.CreateDirectory(externalDirectory);
            var external = Path.Combine(externalDirectory, "subtitle.srt");
            File.WriteAllText(external, "external target bytes must remain unchanged");
            File.SetUnixFileMode(external, UnixFileMode.UserRead);
            var originalHash = SHA256.HashData(File.ReadAllBytes(external));
            _source = Path.Combine(_series.Path, "Old.en.srt");
            File.CreateSymbolicLink(_source, Path.GetRelativePath(_series.Path, external));
            _episodeFile.RelativePath = changeDirectory ? Path.Combine("Season 2", "New.mkv") : "New.mkv";
            _destination = Path.ChangeExtension(Path.Combine(_series.Path, _episodeFile.RelativePath), ".en.srt");
            Directory.CreateDirectory(Path.GetDirectoryName(_destination));
            Action rename = () => Subject.MoveFilesAfterRename(_series, new List<EpisodeFile> { _episodeFile }, true);

            if (failCommit)
            {
                using (var connection = Db.OpenConnection())
                {
                    if (Db.DatabaseType == DatabaseType.SQLite)
                    {
                        connection.Execute("CREATE TRIGGER fail_sidecar_update BEFORE UPDATE ON SubtitleFiles BEGIN SELECT RAISE(ABORT, 'injected sidecar update failure'); END");
                    }
                    else
                    {
                        connection.Execute("CREATE FUNCTION fail_sidecar_update() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'injected sidecar update failure'; END; $$ LANGUAGE plpgsql");
                        connection.Execute("CREATE TRIGGER fail_sidecar_update BEFORE UPDATE ON \"SubtitleFiles\" FOR EACH ROW EXECUTE FUNCTION fail_sidecar_update()");
                    }
                }

                rename.Should().Throw<AggregateException>();
                StoredModel.RelativePath.Should().Be("Old.en.srt");
                new FileInfo(_source).LinkTarget.Should().NotBeNull();
                new FileInfo(_destination).ResolveLinkTarget(true).FullName.Should().Be(external);
                Receipts().Should().ContainSingle();
                using var connectionAfterFailure = Db.OpenConnection();
                connectionAfterFailure.Execute(Db.DatabaseType == DatabaseType.SQLite
                    ? "DROP TRIGGER fail_sidecar_update"
                    : "DROP TRIGGER fail_sidecar_update ON \"SubtitleFiles\"; DROP FUNCTION fail_sidecar_update()");
            }

            Mocker.Resolve<SubtitleService>().MoveFilesAfterRename(_series, new List<EpisodeFile> { _episodeFile }, true);

            new FileInfo(_source).LinkTarget.Should().BeNull();
            new FileInfo(_destination).LinkTarget.Should().Be(Path.GetRelativePath(Path.GetDirectoryName(_destination), external));
            new FileInfo(_destination).ResolveLinkTarget(true).FullName.Should().Be(external);
            SHA256.HashData(File.ReadAllBytes(external)).Should().Equal(originalHash);
            File.GetUnixFileMode(external).Should().Be(UnixFileMode.UserRead);
            StoredModel.RelativePath.Should().Be(Path.GetRelativePath(_series.Path, _destination));
            Receipts().Should().BeEmpty();
            Mocker.GetMock<IDiskProvider>().Verify(d => d.OpenReadStream(It.IsAny<string>()), Times.Never());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_preserve_receipt_and_database_when_destination_bytes_or_owner_changed(bool foreignOwner)
        {
            _interruptAfterMove = true;
            Action rename = () => Subject.MoveFilesAfterRename(_series, new List<EpisodeFile> { _episodeFile }, true);
            rename.Should().Throw<AggregateException>();
            _interruptAfterMove = false;
            if (foreignOwner)
            {
                Db.Insert(new SubtitleFile { SeriesId = 1, SeasonNumber = 1, EpisodeFileId = 20, RelativePath = "New.en.srt", Extension = ".srt", Language = Language.English });
            }
            else
            {
                File.WriteAllText(_destination, "externally replaced subtitle");
            }

            rename.Should().Throw<AggregateException>();

            AllStoredModels.Single(s => s.EpisodeFileId == 10).RelativePath.Should().Be("Old.en.srt");
            Receipts().Should().ContainSingle();
            File.ReadAllText(_destination).Should().Be(foreignOwner ? "original subtitle content" : "externally replaced subtitle");
        }
    }
}
