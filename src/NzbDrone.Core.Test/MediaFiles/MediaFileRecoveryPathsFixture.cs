using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles
{
    public class MediaFileRecoveryPathsFixture : CoreTest
    {
        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FolderExists(It.IsAny<string>())).Returns<string>(Directory.Exists);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FileExists(It.IsAny<string>())).Returns<string>(File.Exists);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.GetFileAttributes(It.IsAny<string>())).Returns<string>(File.GetAttributes);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.WriteAllText(It.IsAny<string>(), It.IsAny<string>())).Callback<string, string>(File.WriteAllText);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.MoveFile(It.IsAny<string>(), It.IsAny<string>(), true)).Callback<string, string, bool>(File.Move);
        }

        [Test]
        public void receipt_write_should_not_follow_replaced_parent_directory()
        {
            PosixOnly();
            var outside = Path.Combine(TempFolder, "outside");
            Directory.CreateDirectory(outside);
            var link = Path.Combine(TempFolder, "receipts");
            Directory.CreateSymbolicLink(link, outside);

            Assert.Throws<IOException>(() => MediaFileRecoveryPaths.WriteJournal(Mocker.GetMock<IDiskProvider>().Object, Path.Combine(link, "import.json"), new { Id = 1 }));

            Directory.GetFiles(outside).Should().BeEmpty();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void receipt_write_should_not_follow_replaced_receipt_or_temporary_file(bool temporary)
        {
            PosixOnly();
            var directory = Path.Combine(TempFolder, "receipts");
            Directory.CreateDirectory(directory);
            var victim = Path.Combine(TempFolder, "private.txt");
            File.WriteAllText(victim, "private bytes");
            var path = Path.Combine(directory, "import.json");
            File.CreateSymbolicLink(temporary ? path + ".new" : path, victim);

            Assert.Throws<IOException>(() => MediaFileRecoveryPaths.WriteJournal(Mocker.GetMock<IDiskProvider>().Object, path, new { Id = 1 }));

            File.ReadAllText(victim).Should().Be("private bytes");
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        public void recovery_should_reject_missing_path_authority(string path)
        {
            Assert.Throws<IOException>(() => MediaFileRecoveryPaths.ValidateContainedPath(Mocker.GetMock<IDiskProvider>().Object, TempFolder, path));
            Assert.Throws<IOException>(() => MediaFileRecoveryPaths.VerifyResolvedPath(Path.Combine(TempFolder, "episode.mkv"), path));
        }

        [Test]
        public void resolving_nested_season_links_should_reuse_only_operation_local_parent_cache()
        {
            PosixOnly();
            var first = Path.Combine(TempFolder, "first");
            var second = Path.Combine(TempFolder, "second");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            var link = Path.Combine(TempFolder, "season");
            Directory.CreateSymbolicLink(link, first);
            Directory.CreateSymbolicLink(Path.Combine(first, "nested"), second);
            var parents = new Dictionary<string, string>();

            MediaFileRecoveryPaths.ResolveFilePath(Path.Combine(link, "nested", "one.mkv"), parents).Should().Be(Path.Combine(second, "one.mkv"));
            MediaFileRecoveryPaths.ResolveFilePath(Path.Combine(link, "nested", "two.mkv"), parents).Should().Be(Path.Combine(second, "two.mkv"));
            parents.Should().ContainSingle();
            Directory.Delete(link);
            Directory.CreateSymbolicLink(link, second);
            MediaFileRecoveryPaths.ResolveFilePath(Path.Combine(link, "one.mkv")).Should().Be(Path.Combine(second, "one.mkv"));
        }
    }
}
