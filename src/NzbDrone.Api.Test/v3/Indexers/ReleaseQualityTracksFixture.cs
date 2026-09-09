using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Download;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;
using Sonarr.Api.V3.Indexers;

namespace NzbDrone.Api.Test.v3.Indexers
{
    [TestFixture]
    public class ReleaseQualityTracksFixture : TestBase<ReleaseController>
    {
        private List<SeriesQualityTrack> _tracks;
        private ReleaseResource _request;

        [SetUp]
        public void Setup()
        {
            var remoteEpisode = new RemoteEpisode
            {
                Release = new ReleaseInfo { Guid = "release-guid", IndexerId = 1 },
                Series = new NzbDrone.Core.Tv.Series { Id = 1 },
                Episodes = new List<Episode> { new Episode { Id = 1, SeriesId = 1 } }
            };
            _tracks = new List<SeriesQualityTrack>
            {
                new SeriesQualityTrack { Id = 10, SeriesId = 1, QualityProfileId = 1, Enabled = true, IsPrimary = true }
            };
            _request = new ReleaseResource { Guid = "release-guid", IndexerId = 1 };

            Mocker.Resolve<ICacheManager>().GetCache<RemoteEpisode>(typeof(ReleaseController), "remoteEpisodes")
                .Set("1_release-guid", remoteEpisode, TimeSpan.FromMinutes(30));
            Mocker.GetMock<ISeriesQualityTrackService>().Setup(s => s.GetEnabledTracks(1)).Returns(_tracks);
            Mocker.GetMock<IDownloadService>().Setup(s => s.DownloadReport(It.IsAny<RemoteEpisode>(), It.IsAny<int?>())).Returns(Task.CompletedTask);
        }

        [Test]
        public async Task legacy_grab_should_automatically_select_sole_profile()
        {
            await Subject.DownloadRelease(_request);

            Mocker.GetMock<IDownloadService>().Verify(s => s.DownloadReport(It.Is<RemoteEpisode>(r => r.TargetQualityTrackIds.Count == 1 && r.TargetQualityTrackIds[0] == 10), null), Times.Once());
        }

        [Test]
        public void legacy_grab_should_reject_ambiguous_multi_profile_series()
        {
            _tracks.Add(new SeriesQualityTrack { Id = 20, SeriesId = 1, Enabled = true });

            Assert.ThrowsAsync<NzbDroneClientException>(() => Subject.DownloadRelease(_request));

            Mocker.GetMock<IDownloadService>().Verify(s => s.DownloadReport(It.IsAny<RemoteEpisode>(), It.IsAny<int?>()), Times.Never());
        }
    }
}
