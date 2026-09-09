using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Extras.Others
{
    [TestFixture]
    public class OtherExtraFileRenamerSafetyFixture : CoreTest<OtherExtraFileRenamer>
    {
        private Series _series;
        private List<OtherExtraFile> _files;
        private string _path;

        [SetUp]
        public void Setup()
        {
            Directory.CreateDirectory(TempFolder);
            _series = new Series { Id = 1, Path = TempFolder };
            _path = Path.Combine(TempFolder, "Episode.nfo");
            File.WriteAllText(_path, "current metadata");
            File.WriteAllText(_path + "-orig", "older metadata");
            _files = new List<OtherExtraFile>
            {
                new OtherExtraFile { Id = 1, EpisodeFileId = 10, RelativePath = "Episode.nfo", Extension = ".nfo" },
                new OtherExtraFile { Id = 2, EpisodeFileId = 10, RelativePath = "Episode.nfo-orig", Extension = ".nfo-orig" }
            };
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FileExists(It.IsAny<string>())).Returns((string path) => File.Exists(path));
            Mocker.GetMock<IDiskProvider>().Setup(d => d.MoveFile(It.IsAny<string>(), It.IsAny<string>(), false))
                .Callback<string, string, bool>((source, destination, _) => File.Move(source, destination));
            Mocker.GetMock<IOtherExtraFileService>().Setup(s => s.FindByPath(1, It.IsAny<string>()))
                .Returns((int _, string path) => _files.SingleOrDefault(f => f.RelativePath == path));
            Mocker.GetMock<IRecycleBinProvider>().Setup(r => r.DeleteFile(It.IsAny<string>(), It.IsAny<string>()))
                .Callback<string, string>((path, _) => File.Move(path, Path.Combine(TempFolder, "recycled.nfo")));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_archive_only_same_owner_metadata_and_preserve_recycled_bytes(bool explicitOwner)
        {
            Subject.RenameOtherExtraFile(_series, _path, explicitOwner ? 10 : null);

            File.Exists(_path).Should().BeFalse();
            File.ReadAllText(_path + "-orig").Should().Be("current metadata");
            File.ReadAllText(Path.Combine(TempFolder, "recycled.nfo")).Should().Be("older metadata");
            _files[0].RelativePath.Should().Be("Episode.nfo-orig");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_preserve_another_versions_current_or_backup_metadata(bool foreignBackup)
        {
            _files[foreignBackup ? 1 : 0].EpisodeFileId = 20;
            Action rename = () => Subject.RenameOtherExtraFile(_series, _path, 10);

            rename.Should().Throw<IOException>();

            File.ReadAllText(_path).Should().Be("current metadata");
            File.ReadAllText(_path + "-orig").Should().Be("older metadata");
            Mocker.GetMock<IRecycleBinProvider>().Verify(r => r.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
        }
    }
}
