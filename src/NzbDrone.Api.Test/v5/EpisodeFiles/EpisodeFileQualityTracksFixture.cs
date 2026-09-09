using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;
using Sonarr.Api.V5.EpisodeFiles;

namespace NzbDrone.Api.Test.v5.EpisodeFiles
{
    [TestFixture]
    public class EpisodeFileQualityTracksFixture : TestBase<EpisodeFileController>
    {
        private List<EpisodeFile> _files;

        [SetUp]
        public void Setup()
        {
            _files = new List<EpisodeFile>
            {
                new EpisodeFile { Id = 100, SeriesId = 1, RelativePath = "first.mkv", Quality = new QualityModel(Quality.HDTV720p) },
                new EpisodeFile { Id = 200, SeriesId = 2, RelativePath = "second.mkv", Quality = new QualityModel(Quality.HDTV720p) }
            };
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFiles(It.IsAny<IEnumerable<int>>())).Returns(_files);
            Mocker.GetMock<ISeriesService>().Setup(s => s.GetSeries(It.IsAny<int>())).Returns<int>(id => new NzbDrone.Core.Tv.Series
            {
                Id = id,
                Path = "/series/" + id,
                QualityProfile = new QualityProfile()
            });
        }

        [Test]
        public void bulk_file_deletion_should_use_each_files_own_series()
        {
            Subject.DeleteEpisodeFiles(new EpisodeFileListResource { EpisodeFileIds = new List<int> { 100, 200 } });

            Mocker.GetMock<IDeleteMediaFiles>().Verify(s => s.DeleteEpisodeFile(It.Is<NzbDrone.Core.Tv.Series>(series => series.Id == 1), _files[0]), Times.Once());
            Mocker.GetMock<IDeleteMediaFiles>().Verify(s => s.DeleteEpisodeFile(It.Is<NzbDrone.Core.Tv.Series>(series => series.Id == 2), _files[1]), Times.Once());
        }

        [Test]
        public void bulk_file_update_should_return_paths_and_profiles_for_each_files_series()
        {
            var result = Subject.SetPropertiesBulk(new List<EpisodeFileResource>
            {
                new EpisodeFileResource { Id = 100 },
                new EpisodeFileResource { Id = 200 }
            }).Value;

            result.Should().ContainSingle(f => f.Id == 100 && f.SeriesId == 1 && f.Path == Path.Combine("/series/1", "first.mkv"));
            result.Should().ContainSingle(f => f.Id == 200 && f.SeriesId == 2 && f.Path == Path.Combine("/series/2", "second.mkv"));
        }
    }
}
