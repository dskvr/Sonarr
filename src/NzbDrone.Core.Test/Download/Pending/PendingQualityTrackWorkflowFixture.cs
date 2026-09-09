using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Jobs;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Download.Pending
{
    [TestFixture]
    public class PendingQualityTrackWorkflowFixture : DbTest<PendingReleaseService, PendingRelease>
    {
        private Series _series;
        private List<Episode> _episodes;
        private bool _seriesExists;

        [SetUp]
        public void Setup()
        {
            _seriesExists = true;
            var primary = new QualityProfile { Id = 1, Items = Qualities.QualityFixture.GetDefaultQualities() };
            var secondary = new QualityProfile { Id = 2, Items = Qualities.QualityFixture.GetDefaultQualities().AsEnumerable().Reverse().ToList() };
            _series = new Series
            {
                Id = 1,
                Title = "Series",
                QualityProfileId = 1,
                QualityProfile = primary,
                QualityTracks = new List<SeriesQualityTrack>
                {
                    new() { Id = 10, SeriesId = 1, QualityProfileId = 1, IsPrimary = true, Enabled = true, QualityProfile = primary },
                    new() { Id = 20, SeriesId = 1, QualityProfileId = 2, Enabled = true, QualityProfile = secondary }
                }
            };
            var file = new EpisodeFile { Id = 100, Quality = new QualityModel(Quality.HDTV1080p) };
            _episodes = new List<Episode>
            {
                new()
                {
                    Id = 1,
                    SeriesId = 1,
                    Series = _series,
                    SeasonNumber = 1,
                    EpisodeNumber = 1,
                    EpisodeFileId = 100,
                    EpisodeFile = file,
                    TrackFiles = new List<EpisodeTrackFile> { new() { EpisodeId = 1, TrackId = 10, EpisodeFileId = 100, EpisodeFile = file } }
                },
                new() { Id = 2, SeriesId = 1, Series = _series, SeasonNumber = 1, EpisodeNumber = 2 }
            };
            Mocker.SetConstant<IPendingReleaseRepository>(Mocker.Resolve<PendingReleaseRepository>());
            Mocker.GetMock<ISeriesService>().Setup(s => s.GetSeries(1)).Returns(() => _seriesExists ? _series : null);
            Mocker.GetMock<ISeriesService>().Setup(s => s.GetSeries(It.IsAny<IEnumerable<int>>()))
                .Returns<IEnumerable<int>>(ids => _seriesExists && ids.Contains(1) ? new List<Series> { _series } : new List<Series>());
            Mocker.GetMock<IParsingService>().Setup(s => s.Map(It.IsAny<ParsedEpisodeInfo>(), It.IsAny<Series>()))
                .Returns<ParsedEpisodeInfo, Series>((parsed, series) => new RemoteEpisode
                {
                    Series = series,
                    Episodes = _episodes.Where(e => parsed.EpisodeNumbers.Contains(e.EpisodeNumber)).ToList(),
                    MappedSeasonNumber = parsed.SeasonNumber
                });
            Mocker.GetMock<ICustomFormatCalculationService>().Setup(s => s.ParseCustomFormat(It.IsAny<RemoteEpisode>(), It.IsAny<long>())).Returns(new List<CustomFormat>());
            Mocker.GetMock<IIndexerStatusService>().Setup(s => s.GetBlockedProviders()).Returns(new List<IndexerStatus>());
            var delay = new DelayProfile { UsenetDelay = 60, PreferredProtocol = DownloadProtocol.Usenet };
            Mocker.GetMock<IDelayProfileService>().Setup(s => s.AllForTags(It.IsAny<HashSet<int>>())).Returns(new List<DelayProfile> { delay });
            Mocker.GetMock<IDelayProfileService>().Setup(s => s.BestForTags(It.IsAny<HashSet<int>>())).Returns(delay);
            Mocker.GetMock<ITaskManager>().Setup(s => s.GetNextExecution(typeof(RssSyncCommand))).Returns(DateTime.UtcNow.AddMinutes(5));
            Mocker.GetMock<IConfigService>().SetupGet(s => s.RssSyncInterval).Returns(15);
            Reload();
        }

        private void Reload()
        {
            Subject.Handle(new ApplicationStartedEvent());
        }

        private DownloadDecision Decision(Quality quality, List<int> targets, int episode = 1, int ageHours = 2)
        {
            return new DownloadDecision(new RemoteEpisode
            {
                Series = _series,
                Episodes = _episodes.Where(e => e.Id == episode).ToList(),
                ParsedEpisodeInfo = new ParsedEpisodeInfo { SeriesTitle = "Series", SeasonNumber = 1, EpisodeNumbers = [episode], Languages = [Language.English], Quality = new QualityModel(quality) },
                Release = new ReleaseInfo { Guid = $"episode-{episode}-{quality.Id}", Title = $"Series.S01E{episode:00}.{quality.Name}", PublishDate = DateTime.UtcNow.AddHours(-ageHours), IndexerId = 1, Indexer = "Local", DownloadProtocol = DownloadProtocol.Usenet },
                TargetQualityTrackIds = targets
            });
        }

        private void Add(Quality quality, List<int> targets, int episode = 1, int ageHours = 2)
        {
            Subject.Add(Decision(quality, targets, episode, ageHours), PendingReleaseReason.Delay);
        }

        [Test]
        public void should_restore_each_shared_pending_target_with_its_own_profile_and_file()
        {
            var shared = Decision(Quality.HDTV1080p, [10, 20]);

            Subject.Add(shared, PendingReleaseReason.Delay);
            Reload();

            AllStoredModels.Should().HaveCount(2);
            var remote = Subject.GetPendingRemoteEpisodes(1);
            remote.Single(r => r.TargetQualityTrackIds.Contains(10)).Episodes.Single().EpisodeFileId.Should().Be(100);
            remote.Single(r => r.TargetQualityTrackIds.Contains(20)).Episodes.Single().HasFile.Should().BeFalse();
            remote.Single(r => r.TargetQualityTrackIds.Contains(20)).Series.QualityProfileId.Should().Be(2);
            var retry = Subject.GetPending().Should().ContainSingle().Which;
            retry.TargetQualityTrackIds.Should().BeEquivalentTo(new[] { 10, 20 });
            retry.TargetQualityTrackSignatures.Keys.Should().BeEquivalentTo(new[] { 10, 20 });
            shared.RemoteEpisode.Series.QualityProfileId.Should().Be(1);
            shared.RemoteEpisode.Episodes.Single().EpisodeFileId.Should().Be(100);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_rank_and_remove_pending_releases_within_each_target(bool obsolete)
        {
            Add(Quality.HDTV1080p, [10]);
            Add(Quality.DVD, [10]);
            Add(Quality.HDTV1080p, [20]);
            Add(Quality.DVD, [20]);
            Add(Quality.HDTV1080p, [10], 2);

            var queue = obsolete ? Subject.GetPendingQueueObsolete() : Subject.GetPendingQueue();
            queue.Should().HaveCount(3);
            var firstEpisode = queue.Where(q => q.RemoteEpisode.Episodes.Any(e => e.Id == 1)).ToList();
            var primary = firstEpisode.Single(q => q.RemoteEpisode.TargetQualityTrackIds.Contains(10));
            primary.Quality.Quality.Should().Be(Quality.HDTV1080p);
            firstEpisode.Single(q => q.RemoteEpisode.TargetQualityTrackIds.Contains(20)).Quality.Quality.Should().Be(Quality.DVD);

            if (obsolete)
            {
                Subject.RemovePendingQueueItemsObsolete(primary.Id);
            }
            else
            {
                Subject.RemovePendingQueueItems(primary.Id);
            }

            Reload();
            AllStoredModels.Should().HaveCount(3);
            AllStoredModels.Should().OnlyContain(p => p.ParsedEpisodeInfo.EpisodeNumbers.Contains(2) || p.AdditionalInfo.TargetQualityTrackIds.Contains(20));
        }

        [Test]
        public void should_use_only_matching_target_and_episodes_for_delay_age()
        {
            Add(Quality.HDTV1080p, [10], ageHours: 12);
            Add(Quality.DVD, [20], ageHours: 2);
            Add(Quality.HDTV1080p, [20], 2, 30);

            Subject.OldestPendingRelease(1, [1], [20]).Release.AgeHours.Should().BeApproximately(2, 0.1);
            Subject.OldestPendingRelease(1, [1]).Release.AgeHours.Should().BeApproximately(12, 0.1);
            Subject.OldestPendingRelease(1, [1], [30]).Should().BeNull();
        }

        [Test]
        public void should_keep_disabled_and_missing_target_identity_after_profile_changes()
        {
            Add(Quality.HDTV1080p, [20]);
            Add(Quality.DVD, [30]);
            _series.QualityTracks.Value[1].Enabled = false;
            _series.QualityTracks.Value[1].QualityProfileId = 99;
            _series.QualityTracks.Value[1].QualityProfile = new QualityProfile { Id = 99, Items = Qualities.QualityFixture.GetDefaultQualities() };

            Reload();

            Subject.GetPending().SelectMany(r => r.TargetQualityTrackIds).Should().BeEquivalentTo(new[] { 20, 30 });
            Subject.GetPendingRemoteEpisodes(1).Single(r => r.TargetQualityTrackIds.Contains(20)).Series.QualityProfileId.Should().Be(99);
            AllStoredModels.SelectMany(p => p.AdditionalInfo.TargetQualityTrackIds).Should().NotContain(10);
        }

        [Test]
        public void should_bind_legacy_pending_to_known_primary_but_keep_empty_intent_empty()
        {
            Add(Quality.HDTV1080p, null);
            Add(Quality.DVD, []);

            var releases = Subject.GetPending();

            releases.Single(r => r.Guid == $"episode-1-{Quality.HDTV1080p.Id}").TargetQualityTrackIds.Should().Equal(10);
            releases.Single(r => r.Guid == $"episode-1-{Quality.DVD.Id}").TargetQualityTrackIds.Should().BeEmpty();
        }

        [Test]
        public void should_omit_removed_series_from_reconstructed_pending_queue()
        {
            Add(Quality.HDTV1080p, [20]);
            _seriesExists = false;

            Reload();

            Subject.GetPendingRemoteEpisodes(1).Should().BeEmpty();
            Subject.GetPendingQueue().Should().BeEmpty();
            AllStoredModels.Should().ContainSingle();
        }

        [Test]
        public void should_preserve_unmatched_episode_target_until_metadata_can_be_resolved()
        {
            Add(Quality.HDTV1080p, [20]);
            _episodes.Clear();

            Reload();

            Subject.GetPendingRemoteEpisodes(1).Should().ContainSingle().Which.TargetQualityTrackIds.Should().Equal(20);
            Subject.GetPendingRemoteEpisodes(1)[0].Episodes.Should().BeEmpty();
            Subject.GetPending().Should().ContainSingle().Which.TargetQualityTrackIds.Should().Equal(20);
        }
    }
}
