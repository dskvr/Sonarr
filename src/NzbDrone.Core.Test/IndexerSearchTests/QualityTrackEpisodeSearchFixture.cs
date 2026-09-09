using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Queue;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    [TestFixture]
    public class QualityTrackEpisodeSearchFixture : CoreTest<EpisodeSearchService>
    {
        private Series _series;
        private List<Episode> _episodes;
        private List<Queue.Queue> _queue;
        private List<RemoteEpisode> _downloads;
        private Func<List<Episode>, List<DownloadDecision>> _decisions;

        [SetUp]
        public void Setup()
        {
            _series = new Series
            {
                Id = 1,
                Monitored = true,
                QualityProfileId = 1,
                QualityProfile = new QualityProfile { Id = 1 },
                QualityTracks = new List<SeriesQualityTrack>
                {
                    new() { Id = 10, SeriesId = 1, QualityProfileId = 1, IsPrimary = true, Enabled = true },
                    new() { Id = 20, SeriesId = 1, QualityProfileId = 2, Enabled = true }
                }
            };
            _episodes = [Episode(1)];
            _queue = [];
            _downloads = [];
            _decisions = episodes => [Decision(episodes, 20)];

            Mocker.GetMock<ISeriesService>().Setup(s => s.GetSeries(1)).Returns(_series);
            Mocker.GetMock<IEpisodeService>().Setup(s => s.GetEpisodeBySeries(1)).Returns(_episodes);
            Mocker.GetMock<IQueueService>().Setup(s => s.GetQueue()).Returns(_queue);
            Mocker.GetMock<ISeriesQualityTrackService>().Setup(s => s.GetAllTracks()).Returns(() => _series.QualityTracks.Value);
            Mocker.GetMock<IEpisodeService>()
                .Setup(s => s.EpisodesWithoutFiles(It.IsAny<PagingSpec<Episode>>(), true, It.IsAny<HashSet<int>>()))
                .Returns<PagingSpec<Episode>, bool, HashSet<int>>((paging, _, _) => Page(paging));
            Mocker.GetMock<IEpisodeCutoffService>()
                .Setup(s => s.EpisodesWhereCutoffUnmet(It.IsAny<PagingSpec<Episode>>(), It.IsAny<HashSet<int>>(), It.IsAny<List<int>>()))
                .Returns<PagingSpec<Episode>, HashSet<int>, List<int>>((paging, _, _) => Page(paging));
            Mocker.GetMock<ISearchForReleases>()
                .Setup(s => s.EpisodeSearch(It.IsAny<int>(), It.IsAny<bool>(), false))
                .Returns<int, bool, bool>((id, _, _) => Task.FromResult(_decisions([_episodes.Single(e => e.Id == id)])));
            Mocker.GetMock<ISearchForReleases>()
                .Setup(s => s.EpisodeSearch(It.IsAny<Episode>(), It.IsAny<bool>(), false))
                .Returns<Episode, bool, bool>((episode, _, _) => Task.FromResult(_decisions([episode])));
            Mocker.GetMock<ISearchForReleases>()
                .Setup(s => s.SeasonSearch(1, It.IsAny<int>(), It.IsAny<List<Episode>>(), It.IsAny<bool>(), It.IsAny<bool>(), false))
                .Returns<int, int, List<Episode>, bool, bool, bool>((_, _, episodes, _, _, _) => Task.FromResult(_decisions(episodes)));
            Mocker.GetMock<IPrioritizeDownloadDecision>().Setup(s => s.PrioritizeDecisions(It.IsAny<List<DownloadDecision>>()))
                .Returns<List<DownloadDecision>>(decisions => decisions);
            Mocker.GetMock<IDownloadService>().Setup(s => s.DownloadReport(It.IsAny<RemoteEpisode>(), null))
                .Callback<RemoteEpisode, int?>((remote, _) => _downloads.Add(remote)).Returns(Task.CompletedTask);
            Mocker.SetConstant<IProcessDownloadDecisions>(Mocker.Resolve<ProcessDownloadDecisions>());
        }

        private Episode Episode(int id, int season = 1)
        {
            return new Episode
            {
                Id = id,
                SeriesId = 1,
                Series = _series,
                SeasonNumber = season,
                Monitored = true,
                AirDateUtc = DateTime.UtcNow.AddDays(-1),
                TrackFiles = new List<EpisodeTrackFile>()
            };
        }

        private DownloadDecision Decision(List<Episode> episodes, int target)
        {
            return new DownloadDecision(new RemoteEpisode
            {
                Series = _series,
                Episodes = episodes,
                Release = new ReleaseInfo { Guid = "episode-" + episodes[0].Id, Title = "Release" },
                TargetQualityTrackIds = [target]
            });
        }

        private PagingSpec<Episode> Page(PagingSpec<Episode> paging)
        {
            paging.Records = _episodes.Where(e => paging.FilterExpressions.All(p => p.Compile()(e))).ToList();
            return paging;
        }

        private void Queued(params int[] targets)
        {
            _queue.Add(new Queue.Queue
            {
                Episodes = [_episodes[0]],
                RemoteEpisode = new RemoteEpisode { Series = _series, Episodes = [_episodes[0]], TargetQualityTrackIds = targets.ToList() }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_search_missing_or_cutoff_secondary_when_primary_is_already_queued(bool cutoff)
        {
            Queued(10);

            if (cutoff)
            {
                Subject.Execute(new CutoffUnmetEpisodeSearchCommand(1));
            }
            else
            {
                _episodes[0].EpisodeFileId = 50;
                _episodes[0].TrackFiles.Value.Add(new EpisodeTrackFile { EpisodeId = 1, TrackId = 10, EpisodeFileId = 50 });
                Subject.Execute(new MissingEpisodeSearchCommand(1));
            }

            _downloads.Should().ContainSingle().Which.TargetQualityTrackIds.Should().Equal(20);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_skip_search_when_every_enabled_track_is_queued(bool disableSecondary)
        {
            _series.QualityTracks.Value[1].Enabled = !disableSecondary;
            Queued(disableSecondary ? [10] : [10, 20]);

            Subject.Execute(new MissingEpisodeSearchCommand(1));

            _downloads.Should().BeEmpty();
        }

        [Test]
        public void should_use_queue_series_when_episode_projection_has_no_series()
        {
            Queued(10);
            _episodes[0].Series = null;

            Subject.Execute(new MissingEpisodeSearchCommand(1));

            _downloads.Should().ContainSingle().Which.TargetQualityTrackIds.Should().Equal(20);
        }

        [Test]
        public void should_keep_legacy_queue_suppression_without_track_metadata()
        {
            _series.QualityTracks = null;
            Queued(10);

            Subject.Execute(new MissingEpisodeSearchCommand(1));

            _downloads.Should().BeEmpty();
        }

        [Test]
        public void should_search_only_aired_monitored_missing_versions()
        {
            _episodes.Add(Episode(2));
            _episodes[1].Monitored = false;
            _episodes.Add(Episode(3));
            _episodes[2].AirDateUtc = null;
            _episodes.Add(Episode(4));
            _episodes[3].AirDateUtc = DateTime.UtcNow.AddDays(1);
            _episodes.Add(Episode(5));
            _episodes[4].TrackFiles.Value.AddRange([new() { TrackId = 10 }, new() { TrackId = 20 }]);

            Subject.Execute(new MissingEpisodeSearchCommand(1));

            _downloads.Should().ContainSingle().Which.Episodes.Select(e => e.Id).Should().Equal(1);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_group_bulk_search_by_season_and_keep_other_groups_after_failure(bool seasonFailure)
        {
            _episodes.Add(Episode(2));
            _episodes.Add(Episode(3, 2));
            _decisions = episodes =>
            {
                if ((episodes.Count > 1) == seasonFailure)
                {
                    throw new InvalidOperationException("Indexer temporarily unavailable");
                }

                return [Decision(episodes, 20)];
            };

            Subject.Execute(new MissingEpisodeSearchCommand(1));

            _downloads.Should().ContainSingle();
            _downloads[0].Episodes.Select(e => e.Id).Should().Equal(seasonFailure ? [3] : [1, 2]);
            ExceptionVerification.ExpectedErrors(1);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_filter_additional_quality_profile_in_bulk_search(bool cutoff)
        {
            if (cutoff)
            {
                Subject.Execute(new CutoffUnmetEpisodeSearchCommand { QualityProfileIds = [2], SeriesIds = [1], SeriesType = [SeriesTypes.Standard] });
            }
            else
            {
                Subject.Execute(new MissingEpisodeSearchCommand { QualityProfileIds = [2], SeriesIds = [1], SeriesType = [SeriesTypes.Standard] });
            }

            _downloads.Should().ContainSingle().Which.TargetQualityTrackIds.Should().Equal(20);
        }

        [Test]
        public void should_exclude_disabled_profile_and_unselected_series_from_bulk_filter()
        {
            _series.QualityTracks.Value[1].Enabled = false;
            Subject.Execute(new MissingEpisodeSearchCommand { QualityProfileIds = [2] });
            Subject.Execute(new CutoffUnmetEpisodeSearchCommand { SeriesId = 2, QualityProfileIds = [1] });

            _downloads.Should().BeEmpty();
        }

        [Test]
        public void should_keep_primary_profile_filter_compatible_without_track_rows()
        {
            _series.QualityTracks.Value.Clear();

            Subject.Execute(new MissingEpisodeSearchCommand { QualityProfileIds = [1], SeriesIds = null, SeriesType = null });

            _downloads.Should().ContainSingle();
        }

        [Test]
        public void should_search_unmonitored_series_when_episode_itself_is_monitored()
        {
            _series.Monitored = false;

            Subject.Execute(new CutoffUnmetEpisodeSearchCommand { Monitored = false, QualityProfileIds = null, SeriesIds = null, SeriesType = null });

            _downloads.Should().ContainSingle();
        }

        [Test]
        public void should_not_suppress_version_search_for_unidentified_queue_item()
        {
            _queue.Add(new Queue.Queue { Episodes = [_episodes[0]], RemoteEpisode = null });

            Subject.Execute(new MissingEpisodeSearchCommand(1));

            _downloads.Should().ContainSingle();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_allow_explicit_unmonitored_bulk_search(bool cutoff)
        {
            _episodes[0].Monitored = false;

            if (cutoff)
            {
                Subject.Execute(new CutoffUnmetEpisodeSearchCommand { Monitored = false });
            }
            else
            {
                Subject.Execute(new MissingEpisodeSearchCommand { Monitored = false });
            }

            _downloads.Should().ContainSingle();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_filter_requested_track_before_real_download_processing(bool explicitTarget)
        {
            _decisions = episodes =>
            {
                var aggregate = Decision(episodes, 10);
                aggregate.QualityTrackDecisions = [Decision(episodes, 10), Decision(episodes, 20)];
                return [aggregate];
            };

            var command = new EpisodeSearchCommand([1]) { TargetQualityTrackIds = explicitTarget ? [20] : null, Trigger = CommandTrigger.Manual };
            Subject.Execute(command);

            _downloads.Should().ContainSingle().Which.TargetQualityTrackIds.Should().BeEquivalentTo(explicitTarget ? new[] { 20 } : new[] { 10, 20 });
            command.TargetQualityTrackIds.Should().BeEquivalentTo(explicitTarget ? new[] { 20 } : null);
        }

        [Test]
        public void should_not_acquire_any_version_for_empty_explicit_target_set()
        {
            Subject.Execute(new EpisodeSearchCommand([1]) { TargetQualityTrackIds = [] });

            _downloads.Should().BeEmpty();
        }
    }
}
