using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.MediaFiles.UpdateEpisodeFileServiceTests
{
    public class UpdateVersionDatesFixture : CoreTest<UpdateEpisodeFileService>
    {
        [Test]
        public void rescan_should_update_each_linked_physical_version_once_and_skip_orphans()
        {
            var series = new Series { Id = 1, Path = TempFolder };
            var files = new List<EpisodeFile>
            {
                new EpisodeFile { Id = 1, SeriesId = 1, RelativePath = "primary.mkv" },
                new EpisodeFile { Id = 2, SeriesId = 1, RelativePath = "secondary.mkv" },
                new EpisodeFile { Id = 3, SeriesId = 1, RelativePath = "orphan.mkv" }
            };
            foreach (var file in files)
            {
                File.WriteAllText(Path.Combine(TempFolder, file.RelativePath), "video bytes");
            }

            var airDate = new DateTime(2021, 6, 2, 12, 0, 0, DateTimeKind.Utc);
            var episode = new Episode { Id = 10, SeriesId = 1, EpisodeFileId = 1, AirDateUtc = airDate };
            Mocker.GetMock<IConfigService>().SetupGet(s => s.FileDate).Returns(FileDateType.UtcAirDate);
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFilesBySeries(1)).Returns(files);
            Mocker.GetMock<IEpisodeService>().Setup(s => s.GetEpisodesByFileId(It.IsAny<int>())).Returns<int>(id => id == 3 ? new List<Episode>() : new List<Episode> { episode });
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FileGetLastWrite(It.IsAny<string>())).Returns<string>(File.GetLastWriteTimeUtc);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FileSetLastWriteTime(It.IsAny<string>(), It.IsAny<DateTime>())).Callback<string, DateTime>(File.SetLastWriteTime);

            Subject.Handle(new SeriesScannedEvent(series, new List<string>()));

            foreach (var file in files.GetRange(0, 2))
            {
                var path = Path.Combine(TempFolder, file.RelativePath);
                File.GetLastWriteTime(path).WithoutTicks().Should().Be(DateTime.SpecifyKind(airDate, DateTimeKind.Local));
                File.ReadAllText(path).Should().Be("video bytes");
            }

            Mocker.GetMock<IDiskProvider>().Verify(s => s.FileSetLastWriteTime(It.IsAny<string>(), It.IsAny<DateTime>()), Times.Exactly(2));
            Mocker.GetMock<IDiskProvider>().Verify(s => s.FileSetLastWriteTime(Path.Combine(TempFolder, "orphan.mkv"), It.IsAny<DateTime>()), Times.Never());
        }
    }
}
