using System.Collections.Generic;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Download.TrackedDownloads
{
    [TestFixture]
    public class TrackedDownloadAlreadyImportedFixture : CoreTest<TrackedDownloadAlreadyImported>
    {
        private List<Episode> _episodes;
        private TrackedDownload _trackedDownload;
        private List<EpisodeHistory> _historyItems;

        [SetUp]
        public void Setup()
        {
            _episodes = new List<Episode>();

            var remoteEpisode = Builder<RemoteEpisode>.CreateNew()
                                                      .With(r => r.Episodes = _episodes)
                                                      .Build();

            var downloadItem = Builder<DownloadClientItem>.CreateNew()
                                                         .Build();

            _trackedDownload = Builder<TrackedDownload>.CreateNew()
                                                       .With(t => t.RemoteEpisode = remoteEpisode)
                                                       .With(t => t.DownloadItem = downloadItem)
                                                       .Build();

            _historyItems = new List<EpisodeHistory>();
        }

        public void GivenEpisodes(int count)
        {
            _episodes.AddRange(Builder<Episode>.CreateListOfSize(count)
                                               .BuildList());
        }

        public void GivenHistoryForEpisode(Episode episode, params EpisodeHistoryEventType[] eventTypes)
        {
            foreach (var eventType in eventTypes)
            {
                _historyItems.Add(
                    Builder<EpisodeHistory>.CreateNew()
                                            .With(h => h.EpisodeId = episode.Id)
                                            .With(h => h.EventType = eventType)
                                            .Build());
            }
        }

        [Test]
        public void should_not_treat_legacy_import_history_as_secondary_target_evidence()
        {
            GivenEpisodes(1);
            _trackedDownload.RemoteEpisode.Series = new Series
            {
                QualityTracks = new List<SeriesQualityTrack> { new() { Id = 10, IsPrimary = true } }
            };
            _trackedDownload.RemoteEpisode.TargetQualityTrackIds = [20];
            GivenHistoryForEpisode(_episodes[0], EpisodeHistoryEventType.DownloadFolderImported);

            Subject.IsImported(_trackedDownload, _historyItems).Should().BeFalse();
            _trackedDownload.RemoteEpisode.TargetQualityTrackIds = [10];
            Subject.IsImported(_trackedDownload, _historyItems).Should().BeTrue();
            _trackedDownload.RemoteEpisode.TargetQualityTrackIds = [];
            Subject.IsImported(_trackedDownload, _historyItems).Should().BeFalse();
        }

        [Test]
        public void should_not_complete_shared_download_when_only_one_target_imported()
        {
            GivenEpisodes(1);
            _trackedDownload.RemoteEpisode.TargetQualityTrackIds = [10, 20];
            GivenHistoryForEpisode(_episodes[0], EpisodeHistoryEventType.DownloadFolderImported, EpisodeHistoryEventType.Grabbed);
            _historyItems[0].Data["qualityTrackIds"] = "[10]";
            _historyItems[1].Data["qualityTrackIds"] = "[10,20]";

            Subject.IsImported(_trackedDownload, _historyItems).Should().BeFalse();
        }

        [Test]
        public void should_combine_separate_target_imports_in_history()
        {
            GivenEpisodes(1);
            _trackedDownload.RemoteEpisode.TargetQualityTrackIds = [10, 20];
            GivenHistoryForEpisode(_episodes[0], EpisodeHistoryEventType.DownloadFolderImported, EpisodeHistoryEventType.DownloadFolderImported, EpisodeHistoryEventType.Grabbed);
            _historyItems[0].Data["qualityTrackIds"] = "[20]";
            _historyItems[1].Data["qualityTrackIds"] = "[10]";
            _historyItems[2].Data["qualityTrackIds"] = "[10,20]";

            Subject.IsImported(_trackedDownload, _historyItems).Should().BeTrue();
        }

        [Test]
        public void should_return_false_if_there_is_no_history()
        {
            GivenEpisodes(1);

            Subject.IsImported(_trackedDownload, _historyItems)
                   .Should()
                   .BeFalse();
        }

        [Test]
        public void should_return_false_if_single_episode_download_is_not_imported()
        {
            GivenEpisodes(1);

            GivenHistoryForEpisode(_episodes[0], EpisodeHistoryEventType.Grabbed);

            Subject.IsImported(_trackedDownload, _historyItems)
                   .Should()
                   .BeFalse();
        }

        [Test]
        public void should_return_false_if_no_episode_in_multi_episode_download_is_imported()
        {
            GivenEpisodes(2);

            GivenHistoryForEpisode(_episodes[0], EpisodeHistoryEventType.Grabbed);
            GivenHistoryForEpisode(_episodes[1], EpisodeHistoryEventType.Grabbed);

            Subject.IsImported(_trackedDownload, _historyItems)
                   .Should()
                   .BeFalse();
        }

        [Test]
        public void should_should_return_false_if_only_one_episode_in_multi_episode_download_is_imported()
        {
            GivenEpisodes(2);

            GivenHistoryForEpisode(_episodes[0], EpisodeHistoryEventType.DownloadFolderImported, EpisodeHistoryEventType.Grabbed);
            GivenHistoryForEpisode(_episodes[1], EpisodeHistoryEventType.Grabbed);

            Subject.IsImported(_trackedDownload, _historyItems)
                   .Should()
                   .BeFalse();
        }

        [Test]
        public void should_return_true_if_single_episode_download_is_imported()
        {
            GivenEpisodes(1);

            GivenHistoryForEpisode(_episodes[0], EpisodeHistoryEventType.DownloadFolderImported, EpisodeHistoryEventType.Grabbed);

            Subject.IsImported(_trackedDownload, _historyItems)
                   .Should()
                   .BeTrue();
        }

        [Test]
        public void should_return_true_if_multi_episode_download_is_imported()
        {
            GivenEpisodes(2);

            GivenHistoryForEpisode(_episodes[0], EpisodeHistoryEventType.DownloadFolderImported, EpisodeHistoryEventType.Grabbed);
            GivenHistoryForEpisode(_episodes[1], EpisodeHistoryEventType.DownloadFolderImported, EpisodeHistoryEventType.Grabbed);

            Subject.IsImported(_trackedDownload, _historyItems)
                   .Should()
                   .BeTrue();
        }
    }
}
