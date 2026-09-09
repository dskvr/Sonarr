using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;
using Sonarr.Api.V5.Release;

namespace NzbDrone.Api.Test.v5.Release
{
    [TestFixture]
    public class ReleaseQualityTracksFixture : TestBase<ReleaseController>
    {
        private RemoteEpisode _remoteEpisode;
        private List<SeriesQualityTrack> _tracks;
        private ReleaseGrabResource _request;

        [SetUp]
        public void Setup()
        {
            _remoteEpisode = new RemoteEpisode
            {
                Release = new ReleaseInfo { Guid = "release-guid", IndexerId = 1 },
                ParsedEpisodeInfo = new ParsedEpisodeInfo(),
                Series = new NzbDrone.Core.Tv.Series { Id = 1 },
                Episodes = new List<Episode> { new Episode { Id = 1, SeriesId = 1 } },
                TargetQualityTrackIds = new List<int> { 10, 20 }
            };
            _tracks = new List<SeriesQualityTrack>
            {
                new SeriesQualityTrack { Id = 10, SeriesId = 1, QualityProfileId = 1, Enabled = true, IsPrimary = true },
                new SeriesQualityTrack { Id = 20, SeriesId = 1, QualityProfileId = 2, Enabled = true }
            };
            _request = new ReleaseGrabResource { Guid = "release-guid", IndexerId = 1 };

            Mocker.Resolve<ICacheManager>().GetCache<RemoteEpisode>(typeof(ReleaseController), "remoteEpisodes")
                .Set("1_release-guid", _remoteEpisode, TimeSpan.FromMinutes(30));
            Mocker.GetMock<ISeriesQualityTrackService>().Setup(s => s.GetEnabledTracks(1)).Returns(_tracks);
            Mocker.GetMock<IDownloadService>().Setup(s => s.DownloadReport(It.IsAny<RemoteEpisode>(), It.IsAny<int?>())).Returns(Task.CompletedTask);
        }

        [Test]
        public async Task sole_enabled_profile_should_be_selected_without_request_targets()
        {
            _tracks.RemoveAt(1);
            _remoteEpisode.TargetQualityTrackIds.Clear();

            await Subject.DownloadRelease(_request);

            Mocker.GetMock<IDownloadService>().Verify(s => s.DownloadReport(It.Is<RemoteEpisode>(r => r.TargetQualityTrackIds.Count == 1 && r.TargetQualityTrackIds[0] == 10 && r.Release.TargetQualityTrackIds[0] == 10), null), Times.Once());
        }

        [Test]
        public async Task omitted_targets_should_download_cached_accepted_targets_once()
        {
            await Subject.DownloadRelease(_request);

            Mocker.GetMock<IDownloadService>().Verify(s => s.DownloadReport(It.Is<RemoteEpisode>(r => r.TargetQualityTrackIds.Count == 2), null), Times.Once());
        }

        [Test]
        public async Task explicit_subset_should_not_mutate_search_cache()
        {
            _request.TargetQualityTrackIds = new List<int> { 20 };
            _remoteEpisode.TargetQualityTrackSignatures = new Dictionary<int, string> { [10] = "original-hd", [20] = "original-uhd" };

            await Subject.DownloadRelease(_request);

            Mocker.GetMock<IDownloadService>().Verify(s => s.DownloadReport(It.Is<RemoteEpisode>(r => r.TargetQualityTrackIds.Count == 1 && r.TargetQualityTrackIds[0] == 20 && r.TargetQualityTrackSignatures.Count == 1 && r.TargetQualityTrackSignatures[20] == "original-uhd" && r.Release.TargetQualityTrackSignatures[20] == "original-uhd"), null), Times.Once());
            _remoteEpisode.TargetQualityTrackIds.Should().Equal(10, 20);
            _remoteEpisode.Release.TargetQualityTrackIds.Should().BeNull();
            _remoteEpisode.TargetQualityTrackSignatures.Should().HaveCount(2);
            _remoteEpisode.Release.TargetQualityTrackSignatures.Should().BeNull();
        }

        [TestCase(new int[0])]
        [TestCase(new[] { 10, 10 })]
        [TestCase(new[] { 99 })]
        [TestCase(new[] { -1 })]
        [TestCase(new[] { 0 })]
        public void invalid_target_selection_should_not_download(int[] ids)
        {
            _request.TargetQualityTrackIds = new List<int>(ids);

            Assert.ThrowsAsync<NzbDroneClientException>(() => Subject.DownloadRelease(_request));

            Mocker.GetMock<IDownloadService>().Verify(s => s.DownloadReport(It.IsAny<RemoteEpisode>(), It.IsAny<int?>()), Times.Never());
        }

        [Test]
        public void disabled_cached_target_should_not_redirect_to_another_profile()
        {
            _tracks.RemoveAt(1);
            _tracks.Add(new SeriesQualityTrack { Id = 30, SeriesId = 1, Enabled = true });

            Assert.ThrowsAsync<NzbDroneClientException>(() => Subject.DownloadRelease(_request));

            Mocker.GetMock<IDownloadService>().Verify(s => s.DownloadReport(It.IsAny<RemoteEpisode>(), It.IsAny<int?>()), Times.Never());
        }

        [Test]
        public void disabled_cached_target_should_not_redirect_to_sole_remaining_profile()
        {
            _tracks.RemoveAt(1);
            _remoteEpisode.TargetQualityTrackIds = new List<int> { 20 };

            Assert.ThrowsAsync<NzbDroneClientException>(() => Subject.DownloadRelease(_request));

            Mocker.GetMock<IDownloadService>().Verify(s => s.DownloadReport(It.IsAny<RemoteEpisode>(), It.IsAny<int?>()), Times.Never());
        }

        [Test]
        public void ambiguous_multi_profile_force_grab_should_require_explicit_selection()
        {
            _remoteEpisode.TargetQualityTrackIds.Clear();

            Assert.ThrowsAsync<NzbDroneClientException>(() => Subject.DownloadRelease(_request));

            Mocker.GetMock<IDownloadService>().Verify(s => s.DownloadReport(It.IsAny<RemoteEpisode>(), It.IsAny<int?>()), Times.Never());
        }

        [Test]
        public async Task explicit_multi_profile_force_grab_should_preserve_override_behavior()
        {
            _remoteEpisode.TargetQualityTrackIds.Clear();
            _request.TargetQualityTrackIds = new List<int> { 20 };

            await Subject.DownloadRelease(_request);

            Mocker.GetMock<IDownloadService>().Verify(s => s.DownloadReport(It.Is<RemoteEpisode>(r => r.TargetQualityTrackIds[0] == 20), null), Times.Once());
        }

        [Test]
        public void cross_series_episode_selection_should_not_download()
        {
            _remoteEpisode.Episodes[0].SeriesId = 2;

            Assert.ThrowsAsync<NzbDroneClientException>(() => Subject.DownloadRelease(_request));

            Mocker.GetMock<IDownloadService>().Verify(s => s.DownloadReport(It.IsAny<RemoteEpisode>(), It.IsAny<int?>()), Times.Never());
        }

        [Test]
        public void release_resource_should_preserve_child_rejections_and_track_local_scores()
        {
            var first = _remoteEpisode.Clone();
            first.Series = new NzbDrone.Core.Tv.Series { Id = 1, QualityProfileId = 1 };
            first.TargetQualityTrackIds = new List<int> { 10 };
            first.CustomFormatScore = 100;
            var second = _remoteEpisode.Clone();
            second.Series = new NzbDrone.Core.Tv.Series { Id = 1, QualityProfileId = 2 };
            second.TargetQualityTrackIds = new List<int> { 20 };
            second.CustomFormatScore = -10;
            _remoteEpisode.TargetQualityTrackIds = new List<int> { 10 };
            var decision = new DownloadDecision(_remoteEpisode)
            {
                QualityTrackDecisions = new List<DownloadDecision>
                {
                    new DownloadDecision(first),
                    new DownloadDecision(second, new DownloadRejection(DownloadRejectionReason.CustomFormatMinimumScore, "Below profile minimum"))
                }
            };

            var resource = decision.ToResource();

            resource.Decision.Approved.Should().BeTrue();
            resource.TargetQualityTrackIds.Should().Equal(10);
            resource.QualityTrackDecisions.Should().ContainSingle(d => d.TrackId == 10 && d.QualityProfileId == 1 && d.CustomFormatScore == 100 && d.Decision.Approved);
            resource.QualityTrackDecisions.Should().ContainSingle(d => d.TrackId == 20 && d.QualityProfileId == 2 && d.CustomFormatScore == -10 && d.Decision.Rejected);
            resource.QualityTrackDecisions[1].Decision.Rejections.Should().ContainSingle(r => r.Message == "Below profile minimum");
        }
    }
}
