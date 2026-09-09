using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.DecisionEngineTests
{
    [TestFixture]
    public class QualityTrackDecisionFixture : CoreTest<DownloadDecisionMaker>
    {
        private RemoteEpisode _remote;
        private List<SeriesQualityTrack> _tracks;
        private List<EpisodeTrackFile> _links;
        private ReleaseInfo _release;

        [SetUp]
        public void Setup()
        {
            _tracks = new List<SeriesQualityTrack>
            {
                new() { Id = 10, SeriesId = 1, QualityProfileId = 1, IsPrimary = true, Enabled = true, QualityProfile = Profile(1) },
                new() { Id = 20, SeriesId = 1, QualityProfileId = 2, Enabled = true, QualityProfile = Profile(2) }
            };
            _links = [];
            _remote = new RemoteEpisode
            {
                Series = new Series { Id = 1, QualityProfileId = 1, QualityProfile = _tracks[0].QualityProfile, QualityTracks = _tracks },
                Episodes = [new Episode { Id = 1, SeriesId = 1 }],
                ParsedEpisodeInfo = new ParsedEpisodeInfo { Quality = new QualityModel(Quality.HDTV1080p) }
            };
            _release = new ReleaseInfo { Title = "The.Office.S03E01.1080p.HDTV-GROUP" };

            Mocker.GetMock<ISeriesQualityTrackService>().Setup(s => s.GetEnabledTracks(1)).Returns(() => _tracks.Where(t => t.Enabled).ToList());
            Mocker.GetMock<IEpisodeTrackFileService>().Setup(s => s.GetForSeries(1)).Returns(_links);
            Mocker.GetMock<IParsingService>()
                .Setup(s => s.Map(It.IsAny<ParsedEpisodeInfo>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<SearchCriteriaBase>()))
                .Returns(_remote);
            Mocker.GetMock<ICustomFormatCalculationService>()
                .Setup(s => s.ParseCustomFormat(It.IsAny<RemoteEpisode>(), It.IsAny<long>())).Returns(new List<CustomFormat>());
            Mocker.GetMock<ICustomFormatCalculationService>()
                .Setup(s => s.ParseCustomFormat(It.IsAny<EpisodeFile>())).Returns(new List<CustomFormat>());
            Mocker.SetConstant<IEnumerable<IDownloadDecisionEngineSpecification>>(
                [Mocker.Resolve<QualityAllowedByProfileSpecification>(), Mocker.Resolve<UpgradeDiskSpecification>()]);
        }

        private QualityProfile Profile(int id)
        {
            return new QualityProfile
            {
                Id = id,
                UpgradeAllowed = true,
                Items = Qualities.QualityFixture.GetDefaultQualities(),
                Cutoff = Quality.HDTV1080p.Id,
                CutoffFormatScore = 0
            };
        }

        [TestCase(10, 20)]
        [TestCase(20, 10)]
        public void should_acquire_missing_track_when_other_track_has_file(int present, int missing)
        {
            var file = new EpisodeFile { Id = 50, Quality = new QualityModel(Quality.HDTV1080p) };
            _links.Add(new EpisodeTrackFile { EpisodeId = 1, TrackId = present, EpisodeFileId = 50, EpisodeFile = file });
            _remote.Episodes[0].EpisodeFileId = 50;
            _remote.Episodes[0].EpisodeFile = file;

            var result = Subject.GetRssDecision([_release]).Single();

            result.Approved.Should().BeTrue();
            result.RemoteEpisode.TargetQualityTrackIds.Should().Equal(missing);
            result.QualityTrackDecisions.Single(d => d.RemoteEpisode.TargetQualityTrackIds.Contains(present)).Rejected.Should().BeTrue();
            _remote.Episodes[0].EpisodeFileId.Should().Be(50);
        }

        [Test]
        public void should_load_track_state_once_per_series_and_refresh_next_batch()
        {
            Subject.GetRssDecision([_release, _release]);

            Mocker.GetMock<ISeriesQualityTrackService>().Verify(s => s.GetEnabledTracks(1), Times.Once());
            Mocker.GetMock<IEpisodeTrackFileService>().Verify(s => s.GetForSeries(1), Times.Once());
            _tracks[1].Enabled = false;
            Subject.GetRssDecision([_release]).Single().RemoteEpisode.TargetQualityTrackIds.Should().Equal(10);
            Mocker.GetMock<ISeriesQualityTrackService>().Verify(s => s.GetEnabledTracks(1), Times.Exactly(2));
        }

        [Test]
        public void should_evaluate_overlapping_profiles_once_each_and_return_one_release()
        {
            var results = Subject.GetRssDecision([_release]);

            results.Should().ContainSingle();
            results[0].RemoteEpisode.TargetQualityTrackIds.Should().BeEquivalentTo([10, 20]);
            results[0].QualityTrackDecisions.Should().HaveCount(2).And.OnlyContain(d => d.Approved);
        }

        [Test]
        public void should_preserve_saved_targets_when_profile_selection_changes()
        {
            _release.TargetQualityTrackIds = [20];

            var result = Subject.GetRssDecision([_release]).Single();

            result.RemoteEpisode.TargetQualityTrackIds.Should().Equal(20);
            result.QualityTrackDecisions.Should().ContainSingle();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_reject_removed_or_empty_saved_targets(bool empty)
        {
            _tracks[1].Enabled = false;
            _release.TargetQualityTrackIds = empty ? [] : [20];

            var result = Subject.GetRssDecision([_release]).Single();

            result.Rejected.Should().BeTrue();
            result.RemoteEpisode.TargetQualityTrackIds.Should().BeEmpty();
            result.RemoteEpisode.DownloadAllowed.Should().BeFalse();
        }

        [Test]
        public void should_keep_custom_format_scores_local_to_each_profile()
        {
            var format = new CustomFormat { Id = 1, Name = "Preferred" };
            _tracks[0].QualityProfile.Value.FormatItems = [new ProfileFormatItem { Format = format, Score = -100 }];
            _tracks[0].QualityProfile.Value.MinFormatScore = 0;
            _tracks[1].QualityProfile.Value.FormatItems = [new ProfileFormatItem { Format = format, Score = 100 }];
            Mocker.GetMock<ICustomFormatCalculationService>()
                .Setup(s => s.ParseCustomFormat(It.IsAny<RemoteEpisode>(), It.IsAny<long>())).Returns(new List<CustomFormat> { format });
            Mocker.SetConstant<IEnumerable<IDownloadDecisionEngineSpecification>>([Mocker.Resolve<CustomFormatAllowedbyProfileSpecification>()]);

            var result = Subject.GetRssDecision([_release]).Single();

            result.RemoteEpisode.TargetQualityTrackIds.Should().Equal(20);
            result.QualityTrackDecisions[0].RemoteEpisode.CustomFormatScore.Should().Be(-100);
            result.QualityTrackDecisions[1].RemoteEpisode.CustomFormatScore.Should().Be(100);
        }
    }
}
