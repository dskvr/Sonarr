using System.Linq;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Download
{
    public class RedownloadFailedDownloadService : IHandle<DownloadFailedEvent>
    {
        private readonly IConfigService _configService;
        private readonly IEpisodeService _episodeService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly ISeriesQualityTrackService _qualityTrackService;
        private readonly Logger _logger;

        public RedownloadFailedDownloadService(IConfigService configService,
                                               IEpisodeService episodeService,
                                               IManageCommandQueue commandQueueManager,
                                               ISeriesQualityTrackService qualityTrackService,
                                               Logger logger)
        {
            _configService = configService;
            _episodeService = episodeService;
            _commandQueueManager = commandQueueManager;
            _qualityTrackService = qualityTrackService;
            _logger = logger;
        }

        [EventHandleOrder(EventHandleOrder.Last)]
        public void Handle(DownloadFailedEvent message)
        {
            if (message.SkipRedownload)
            {
                _logger.Debug("Skip redownloading requested by user");
                return;
            }

            if (!_configService.AutoRedownloadFailed)
            {
                _logger.Debug("Auto redownloading failed episodes is disabled");
                return;
            }

            if (message.ReleaseSource == ReleaseSourceType.InteractiveSearch && !_configService.AutoRedownloadFailedFromInteractiveSearch)
            {
                _logger.Debug("Auto redownloading failed episodes from interactive search is disabled");
                return;
            }

            var targets = message.TrackedDownload?.RemoteEpisode?.TargetQualityTrackIds ?? QualityTrackSnapshot.ReadTargets(message.Data, "qualityTrackIds");

            if (targets == null)
            {
                var primary = _qualityTrackService.GetTracks(message.SeriesId)?.SingleOrDefault(t => t.IsPrimary);
                targets = primary == null ? null : [primary.Id];
            }

            if (targets?.Count == 0)
            {
                _logger.Debug("Saved quality profile targets are invalid, skipping automatic retry");
                return;
            }

            if (message.EpisodeIds.Count == 1)
            {
                _logger.Debug("Failed download only contains one episode, searching again");

                _commandQueueManager.Push(new EpisodeSearchCommand(message.EpisodeIds) { TargetQualityTrackIds = targets });

                return;
            }

            var seasonNumber = _episodeService.GetEpisode(message.EpisodeIds.First()).SeasonNumber;
            var episodesInSeason = _episodeService.GetEpisodesBySeason(message.SeriesId, seasonNumber);

            if (message.EpisodeIds.Count == episodesInSeason.Count)
            {
                _logger.Debug("Failed download was entire season, searching again");

                _commandQueueManager.Push(new SeasonSearchCommand
                {
                    SeriesId = message.SeriesId,
                    SeasonNumber = seasonNumber,
                    TargetQualityTrackIds = targets
                });

                return;
            }

            _logger.Debug("Failed download contains multiple episodes, probably a double episode, searching again");

            _commandQueueManager.Push(new EpisodeSearchCommand(message.EpisodeIds) { TargetQualityTrackIds = targets });
        }
    }
}
