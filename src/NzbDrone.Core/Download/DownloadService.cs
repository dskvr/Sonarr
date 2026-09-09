using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.EnsureThat;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Common.TPL;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Download
{
    public interface IDownloadService
    {
        Task DownloadReport(RemoteEpisode remoteEpisode, int? downloadClientId);
    }

    public class DownloadService : IDownloadService
    {
        private readonly IProvideDownloadClient _downloadClientProvider;
        private readonly IDownloadClientStatusService _downloadClientStatusService;
        private readonly IIndexerFactory _indexerFactory;
        private readonly IIndexerStatusService _indexerStatusService;
        private readonly IRateLimitService _rateLimitService;
        private readonly IEventAggregator _eventAggregator;
        private readonly ISeedConfigProvider _seedConfigProvider;
        private readonly ITrackedDownloadService _trackedDownloadService;
        private readonly Logger _logger;

        public DownloadService(IProvideDownloadClient downloadClientProvider,
                               IDownloadClientStatusService downloadClientStatusService,
                               IIndexerFactory indexerFactory,
                               IIndexerStatusService indexerStatusService,
                               IRateLimitService rateLimitService,
                               IEventAggregator eventAggregator,
                               ISeedConfigProvider seedConfigProvider,
                               ITrackedDownloadService trackedDownloadService,
                               Logger logger)
        {
            _downloadClientProvider = downloadClientProvider;
            _downloadClientStatusService = downloadClientStatusService;
            _indexerFactory = indexerFactory;
            _indexerStatusService = indexerStatusService;
            _rateLimitService = rateLimitService;
            _eventAggregator = eventAggregator;
            _seedConfigProvider = seedConfigProvider;
            _trackedDownloadService = trackedDownloadService;
            _logger = logger;
        }

        public async Task DownloadReport(RemoteEpisode remoteEpisode, int? downloadClientId)
        {
            QualityTrackSnapshot.CaptureSignatures(remoteEpisode);

            if (ReuseTrackedDownload(remoteEpisode, downloadClientId))
            {
                return;
            }

            var filterBlockedClients = remoteEpisode.Release.PendingReleaseReason == PendingReleaseReason.DownloadClientUnavailable;

            var tags = remoteEpisode.Series?.Tags;

            if (downloadClientId.HasValue)
            {
                var specificClient = _downloadClientProvider.Get(downloadClientId.Value);
                await DownloadReport(remoteEpisode, specificClient);

                return;
            }

            var availableClients = _downloadClientProvider.GetDownloadClients(
                remoteEpisode.Release.DownloadProtocol,
                remoteEpisode.Release.IndexerId,
                filterBlockedClients,
                tags).ToList();

            if (!availableClients.Any())
            {
                throw new DownloadClientUnavailableException($"No {remoteEpisode.Release.DownloadProtocol} download client available");
            }

            var triedClients = new HashSet<int>();

            foreach (var downloadClient in availableClients)
            {
                if (triedClients.Contains(downloadClient.Definition.Id))
                {
                    continue;
                }

                try
                {
                    _logger.Debug("Attempting download with client: {0}", downloadClient.Definition.Name);
                    await DownloadReport(remoteEpisode, downloadClient);

                    _downloadClientProvider.ReportSuccessfulDownloadClient(
                        remoteEpisode.Release.DownloadProtocol,
                        downloadClient.Definition.Id);

                    return;
                }
                catch (DownloadClientException ex)
                {
                    _logger.Trace(ex, "Unable to add report to download client: {0}", downloadClient.Definition.Name);
                    triedClients.Add(downloadClient.Definition.Id);
                }
                catch (Exception ex)
                {
                    // Rethrow specific exceptions that should not trigger a fallback
                    if (ex is ReleaseDownloadException)
                    {
                        throw;
                    }

                    _logger.Trace(ex, "Unable to add report to download client: {0}", downloadClient.Definition.Name);
                    triedClients.Add(downloadClient.Definition.Id);
                }
            }

            throw new DownloadClientUnavailableException("All '{0}' download clients failed", remoteEpisode.Release.DownloadProtocol);
        }

        private bool ReuseTrackedDownload(RemoteEpisode remoteEpisode, int? downloadClientId)
        {
            if (remoteEpisode.TargetQualityTrackIds == null || remoteEpisode.TargetQualityTrackIds.Count == 0 || string.IsNullOrEmpty(remoteEpisode.Release.Guid))
            {
                return false;
            }

            var tracked = _trackedDownloadService.GetTrackedDownloads().FirstOrDefault(t =>
                t.IsTrackable &&
                (t.State == TrackedDownloadState.Downloading || t.State == TrackedDownloadState.ImportBlocked || t.State == TrackedDownloadState.ImportPending) &&
                (!downloadClientId.HasValue || t.DownloadClient == downloadClientId.Value) &&
                t.RemoteEpisode?.Series?.Id == remoteEpisode.Series.Id &&
                t.RemoteEpisode.Release?.IndexerId == remoteEpisode.Release.IndexerId &&
                t.RemoteEpisode.Release?.Guid == remoteEpisode.Release.Guid &&
                t.RemoteEpisode.Episodes.Select(e => e.Id).OrderBy(id => id).SequenceEqual(remoteEpisode.Episodes.Select(e => e.Id).OrderBy(id => id)));

            if (tracked == null)
            {
                return false;
            }

            var existingTargets = QualityTrackSnapshot.GetTargets(tracked.RemoteEpisode).ToHashSet();
            var addedTargets = remoteEpisode.TargetQualityTrackIds.Except(existingTargets).ToList();

            if (addedTargets.Count == 0)
            {
                return true;
            }

            var merged = remoteEpisode.Clone();
            merged.TargetQualityTrackIds = existingTargets.Concat(addedTargets).Where(id => id > 0).ToList();
            merged.TargetQualityTrackSignatures = remoteEpisode.TargetQualityTrackSignatures
                .Where(p => !existingTargets.Contains(p.Key))
                .Concat(tracked.RemoteEpisode.TargetQualityTrackSignatures ?? new Dictionary<int, string>())
                .ToDictionary(p => p.Key, p => p.Value);
            var client = tracked.DownloadItem.DownloadClientInfo;
            _eventAggregator.PublishEvent(new EpisodeGrabbedEvent(merged)
            {
                DownloadId = tracked.DownloadItem.DownloadId,
                NewQualityTrackIds = addedTargets,
                DownloadClientId = tracked.DownloadClient,
                DownloadClient = client.Type,
                DownloadClientName = client.Name
            });
            tracked.RemoteEpisode.TargetQualityTrackIds = merged.TargetQualityTrackIds;
            tracked.RemoteEpisode.TargetQualityTrackSignatures = merged.TargetQualityTrackSignatures;
            _logger.Debug("Using queued release '{0}' for additional quality profiles", remoteEpisode.Release.Title);
            return true;
        }

        private async Task DownloadReport(RemoteEpisode remoteEpisode, IDownloadClient downloadClient)
        {
            Ensure.That(remoteEpisode.Series, () => remoteEpisode.Series).IsNotNull();
            Ensure.That(remoteEpisode.Episodes, () => remoteEpisode.Episodes).HasItems();

            var downloadTitle = remoteEpisode.Release.Title;

            if (downloadClient == null)
            {
                throw new DownloadClientUnavailableException($"{remoteEpisode.Release.DownloadProtocol} Download client isn't configured yet");
            }

            // Get the seed configuration for this release.
            remoteEpisode.SeedConfiguration = _seedConfigProvider.GetSeedConfiguration(remoteEpisode);

            // Limit grabs to 2 per second.
            if (remoteEpisode.Release.DownloadUrl.IsNotNullOrWhiteSpace() && !remoteEpisode.Release.DownloadUrl.StartsWith("magnet:"))
            {
                var url = new HttpUri(remoteEpisode.Release.DownloadUrl);
                await _rateLimitService.WaitAndPulseAsync(url.Host, TimeSpan.FromSeconds(2));
            }

            IIndexer indexer = null;

            if (remoteEpisode.Release.IndexerId > 0)
            {
                indexer = _indexerFactory.GetInstance(_indexerFactory.Get(remoteEpisode.Release.IndexerId));
            }

            string downloadClientId;
            try
            {
                downloadClientId = await downloadClient.Download(remoteEpisode, indexer);
                _downloadClientStatusService.RecordSuccess(downloadClient.Definition.Id);
                _indexerStatusService.RecordSuccess(remoteEpisode.Release.IndexerId);
            }
            catch (ReleaseUnavailableException)
            {
                _logger.Trace("Release {0} no longer available on indexer.", remoteEpisode);
                throw;
            }
            catch (ReleaseBlockedException)
            {
                _logger.Trace("Release {0} previously added to blocklist, not sending to download client again.", remoteEpisode);
                throw;
            }
            catch (DownloadClientRejectedReleaseException)
            {
                _logger.Trace("Release {0} rejected by download client, possible duplicate.", remoteEpisode);
                throw;
            }
            catch (ReleaseDownloadException ex)
            {
                if (ex.InnerException is TooManyRequestsException http429)
                {
                    _indexerStatusService.RecordFailure(remoteEpisode.Release.IndexerId, http429.RetryAfter);
                }
                else
                {
                    _indexerStatusService.RecordFailure(remoteEpisode.Release.IndexerId);
                }

                throw;
            }

            var episodeGrabbedEvent = new EpisodeGrabbedEvent(remoteEpisode);
            episodeGrabbedEvent.DownloadClient = downloadClient.Name;
            episodeGrabbedEvent.DownloadClientId = downloadClient.Definition.Id;
            episodeGrabbedEvent.DownloadClientName = downloadClient.Definition.Name;

            if (downloadClientId.IsNotNullOrWhiteSpace())
            {
                episodeGrabbedEvent.DownloadId = downloadClientId;
            }

            _logger.ProgressInfo("Report sent to {0}. Indexer {1}. {2}", downloadClient.Definition.Name, remoteEpisode.Release.Indexer, downloadTitle);
            _eventAggregator.PublishEvent(episodeGrabbedEvent);
        }
    }
}
