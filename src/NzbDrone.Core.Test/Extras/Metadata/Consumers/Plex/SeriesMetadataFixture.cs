using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Consumers.Plex;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Extras.Metadata.Consumers.Plex
{
    [TestFixture]
    public class SeriesMetadataFixture : CoreTest<PlexMetadata>
    {
        [Test]
        public void should_include_secondary_special_once_when_shared_by_tracks()
        {
            var series = new Series { Id = 1, Title = "Series" };
            Mocker.GetMock<IEpisodeService>().Setup(s => s.GetEpisodeBySeries(1)).Returns(new List<Episode>
            {
                new Episode { Id = 1, SeasonNumber = 0, EpisodeNumber = 2, EpisodeFileId = 10 }
            });
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFilesBySeries(1)).Returns(new List<EpisodeFile>
            {
                new EpisodeFile { Id = 10, SeasonNumber = 0, RelativePath = "HD.mkv" },
                new EpisodeFile { Id = 20, SeasonNumber = 0, RelativePath = "UHD.mkv" }
            });
            Mocker.GetMock<IEpisodeTrackFileService>().Setup(s => s.GetForSeries(1)).Returns(new List<EpisodeTrackFile>
            {
                new EpisodeTrackFile { EpisodeId = 1, TrackId = 1, EpisodeFileId = 10 },
                new EpisodeTrackFile { EpisodeId = 1, TrackId = 2, EpisodeFileId = 20 },
                new EpisodeTrackFile { EpisodeId = 1, TrackId = 3, EpisodeFileId = 20 }
            });
            Subject.Definition = new MetadataDefinition { Settings = new PlexMetadataSettings { EpisodeMappings = true } };

            var result = Subject.SeriesMetadata(series, SeriesMetadataReason.Scan);

            result.Contents.Should().Contain("Episode: SP02: HD.mkv").And.Contain("Episode: SP02: UHD.mkv");
            Mocker.GetMock<IEpisodeTrackFileService>().Verify(s => s.GetForSeries(1), Times.Once());
        }
    }
}
