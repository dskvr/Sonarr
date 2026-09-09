using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles.EpisodeImport.Aggregation;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.MediaFiles.EpisodeImport
{
    public interface IMakeImportDecision
    {
        List<ImportDecision> GetImportDecisions(List<string> videoFiles, Series series);
        List<ImportDecision> GetImportDecisions(List<string> videoFiles, Series series, bool filterExistingFiles);
        List<ImportDecision> GetImportDecisions(List<string> videoFiles, Series series, DownloadClientItem downloadClientItem, ParsedEpisodeInfo downloadClientItemInfo, ParsedEpisodeInfo folderInfo, bool sceneSource);
        List<ImportDecision> GetImportDecisions(List<string> videoFiles, Series series, DownloadClientItem downloadClientItem, ParsedEpisodeInfo downloadClientItemInfo, ParsedEpisodeInfo folderInfo, bool sceneSource, bool filterExistingFiles);
        List<ImportDecision> GetImportDecisions(List<string> videoFiles, Series series, DownloadClientItem downloadClientItem, ParsedEpisodeInfo downloadClientItemInfo, ParsedEpisodeInfo folderInfo, bool sceneSource, bool filterExistingFiles, bool resetQualityTrackTargets, List<int> targetQualityTrackIds);
        ImportDecision GetDecision(LocalEpisode localEpisode, DownloadClientItem downloadClientItem);
    }

    public class ImportDecisionMaker : IMakeImportDecision
    {
        private readonly IEnumerable<IImportDecisionEngineSpecification> _specifications;
        private readonly IMediaFileService _mediaFileService;
        private readonly IAggregationService _aggregationService;
        private readonly IDiskProvider _diskProvider;
        private readonly IDetectSample _detectSample;
        private readonly ITrackedDownloadService _trackedDownloadService;
        private readonly ILocalEpisodeCustomFormatCalculationService _formatCalculator;
        private readonly ISeriesQualityTrackService _qualityTrackService;
        private readonly IEpisodeTrackFileService _trackFileService;
        private readonly Logger _logger;

        public ImportDecisionMaker(IEnumerable<IImportDecisionEngineSpecification> specifications,
                                   IMediaFileService mediaFileService,
                                   IAggregationService aggregationService,
                                   IDiskProvider diskProvider,
                                   IDetectSample detectSample,
                                   ITrackedDownloadService trackedDownloadService,
                                   ILocalEpisodeCustomFormatCalculationService formatCalculator,
                                   ISeriesQualityTrackService qualityTrackService,
                                   IEpisodeTrackFileService trackFileService,
                                   Logger logger)
        {
            _specifications = specifications;
            _mediaFileService = mediaFileService;
            _aggregationService = aggregationService;
            _diskProvider = diskProvider;
            _detectSample = detectSample;
            _trackedDownloadService = trackedDownloadService;
            _formatCalculator = formatCalculator;
            _qualityTrackService = qualityTrackService;
            _trackFileService = trackFileService;
            _logger = logger;
        }

        public List<ImportDecision> GetImportDecisions(List<string> videoFiles, Series series)
        {
            return GetImportDecisions(videoFiles, series, false);
        }

        public List<ImportDecision> GetImportDecisions(List<string> videoFiles, Series series, bool filterExistingFiles)
        {
            return GetImportDecisions(videoFiles, series, null, null, null, false, filterExistingFiles);
        }

        public List<ImportDecision> GetImportDecisions(List<string> videoFiles, Series series, DownloadClientItem downloadClientItem, ParsedEpisodeInfo downloadClientItemInfo, ParsedEpisodeInfo folderInfo, bool sceneSource)
        {
            return GetImportDecisions(videoFiles, series, downloadClientItem, downloadClientItemInfo, folderInfo, sceneSource, true);
        }

        public List<ImportDecision> GetImportDecisions(List<string> videoFiles, Series series, DownloadClientItem downloadClientItem, ParsedEpisodeInfo downloadClientItemInfo, ParsedEpisodeInfo folderInfo, bool sceneSource, bool filterExistingFiles)
        {
            return GetImportDecisions(videoFiles, series, downloadClientItem, downloadClientItemInfo, folderInfo, sceneSource, filterExistingFiles, false, null);
        }

        public List<ImportDecision> GetImportDecisions(List<string> videoFiles, Series series, DownloadClientItem downloadClientItem, ParsedEpisodeInfo downloadClientItemInfo, ParsedEpisodeInfo folderInfo, bool sceneSource, bool filterExistingFiles, bool resetQualityTrackTargets, List<int> targetQualityTrackIds)
        {
            var newFiles = filterExistingFiles ? _mediaFileService.FilterExistingFiles(videoFiles.ToList(), series) : videoFiles.ToList();

            _logger.Debug("Analyzing {0}/{1} files.", newFiles.Count, videoFiles.Count);

            // If not importing from a scene source (series folder for example), then assume all files are not samples
            // to avoid using media info on every file needlessly (especially if Analyse Media Files is disabled).
            var nonSampleVideoFileCount = sceneSource ? GetNonSampleVideoFileCount(newFiles, series, downloadClientItemInfo, folderInfo) : videoFiles.Count;

            var decisions = new List<ImportDecision>();

            foreach (var file in newFiles)
            {
                var localEpisode = new LocalEpisode
                {
                    Series = series,
                    DownloadClientEpisodeInfo = downloadClientItemInfo,
                    DownloadItem = downloadClientItem,
                    FolderEpisodeInfo = folderInfo,
                    Path = file,
                    SceneSource = sceneSource,
                    ResetQualityTrackTargets = resetQualityTrackTargets,
                    TargetQualityTrackIds = targetQualityTrackIds?.ToList(),
                    ExistingFile = series.Path.IsParentPath(file),
                    OtherVideoFiles = nonSampleVideoFileCount > 1
                };

                decisions.AddIfNotNull(GetDecision(localEpisode, downloadClientItem, nonSampleVideoFileCount > 1));
            }

            return decisions;
        }

        public ImportDecision GetDecision(LocalEpisode localEpisode, DownloadClientItem downloadClientItem)
        {
            if (localEpisode.Series != null && localEpisode.Episodes.Any(e => e.SeriesId != localEpisode.Series.Id))
            {
                return new ImportDecision(localEpisode, new ImportRejection(ImportRejectionReason.InvalidSeasonOrEpisode, "Selected episodes must belong to the selected series."));
            }

            var tracks = localEpisode.Series == null ? new List<SeriesQualityTrack>() : _qualityTrackService.GetEnabledTracks(localEpisode.Series.Id);
            if (tracks.Count > 0)
            {
                return GetTrackDecision(localEpisode, downloadClientItem, tracks);
            }

            var reasons = _specifications.Select(c => EvaluateSpec(c, localEpisode, downloadClientItem))
                                         .Where(c => c != null);

            return new ImportDecision(localEpisode, reasons.ToArray());
        }

        private ImportDecision GetTrackDecision(LocalEpisode localEpisode, DownloadClientItem downloadClientItem, List<SeriesQualityTrack> tracks)
        {
            var targets = localEpisode.TargetQualityTrackIds;
            var hasSavedTargets = localEpisode.TargetQualityTrackSignatures != null;
            if (!localEpisode.ResetQualityTrackTargets && downloadClientItem?.DownloadId.IsNotNullOrWhiteSpace() == true)
            {
                var remote = _trackedDownloadService.Find(downloadClientItem.DownloadId)?.RemoteEpisode;
                hasSavedTargets = remote?.TargetQualityTrackIds != null;
                if (targets == null || (remote?.TargetQualityTrackIds != null && targets.All(remote.TargetQualityTrackIds.Contains)))
                {
                    localEpisode.TargetQualityTrackSignatures ??= remote?.TargetQualityTrackSignatures;
                    localEpisode.LegacyQualityTrackTarget = remote?.LegacyQualityTrackTarget == true;
                }

                targets ??= remote?.TargetQualityTrackIds;
                if (targets == null && localEpisode.LegacyQualityTrackTarget)
                {
                    targets = tracks.Where(t => t.IsPrimary).Select(t => t.Id).ToList();
                }
            }

            var links = _trackFileService.GetForSeries(localEpisode.Series.Id);
            if (targets == null && localEpisode.ExistingFile && !localEpisode.ResetQualityTrackTargets)
            {
                var existingFile = _mediaFileService.GetFilesWithRelativePath(localEpisode.Series.Id, localEpisode.Series.Path.GetRelativePath(localEpisode.Path)).SingleOrDefault();
                if (existingFile != null)
                {
                    targets = links.Where(l => l.EpisodeFileId == existingFile.Id).Select(l => l.TrackId).Distinct().ToList();
                }
            }

            var candidates = targets == null ? tracks : tracks.Where(t => targets.Contains(t.Id)).ToList();
            var accepted = new List<int>();
            var matchingTargets = new List<int>();
            var rejected = new List<ImportRejection>();

            foreach (var track in candidates)
            {
                var snapshot = localEpisode.Clone();
                snapshot.Series = QualityTrackSnapshot.CreateSeries(localEpisode.Series, track);
                snapshot.Episodes = QualityTrackSnapshot.CreateEpisodes(localEpisode.Episodes, snapshot.Series, track.Id, links);
                snapshot.TargetQualityTrackIds = [track.Id];
                var profile = track.QualityProfile.Value;
                snapshot.CustomFormatScore = profile.CalculateCustomFormatScore(localEpisode.CustomFormats);
                snapshot.OriginalFileNameCustomFormatScore = profile.CalculateCustomFormatScore(localEpisode.OriginalFileNameCustomFormats);

                var criteriaChanged = downloadClientItem != null && hasSavedTargets && !localEpisode.LegacyQualityTrackTarget && !localEpisode.ResetQualityTrackTargets &&
                    (localEpisode.TargetQualityTrackSignatures == null || !localEpisode.TargetQualityTrackSignatures.TryGetValue(track.Id, out var signature) ||
                     signature != QualityTrackSnapshot.ProfileSignature(profile));
                if ((tracks.Count > 1 || criteriaChanged) && (!profile.Items[profile.GetIndex(localEpisode.Quality.Quality).Index].Allowed || snapshot.CustomFormatScore < profile.MinFormatScore))
                {
                    rejected.Add(new ImportRejection(ImportRejectionReason.NotQualityUpgrade, $"File does not meet quality profile '{profile.Name}'."));
                    continue;
                }

                matchingTargets.Add(track.Id);

                var reasons = _specifications.Select(c => EvaluateSpec(c, snapshot, downloadClientItem)).Where(r => r != null).ToList();
                if (reasons.Count == 0)
                {
                    accepted.Add(track.Id);
                }
                else
                {
                    rejected.AddRange(reasons);
                }
            }

            if (targets == null && downloadClientItem != null && accepted.Count > 1)
            {
                localEpisode.TargetQualityTrackIds = new List<int>();
                return new ImportDecision(localEpisode, new ImportRejection(ImportRejectionReason.DecisionError, "Multiple quality profiles match this untracked download. Select the intended profiles."));
            }

            if (accepted.Count > 0)
            {
                localEpisode.TargetQualityTrackIds = accepted;
            }
            else if (targets != null || tracks.Count == 1)
            {
                // An ordinary rejection must not discard a valid selection used for manual overrides or metadata edits.
                localEpisode.TargetQualityTrackIds = candidates.Select(track => track.Id).ToList();
            }
            else
            {
                localEpisode.TargetQualityTrackIds = matchingTargets.Count == 1 ? matchingTargets : new List<int>();
            }

            if (accepted.Count > 0)
            {
                localEpisode.TargetQualityTrackSignatures = tracks.Where(t => accepted.Contains(t.Id))
                    .ToDictionary(t => t.Id, t => QualityTrackSnapshot.ProfileSignature(t.QualityProfile.Value));
                localEpisode.ExpectedTrackFiles = localEpisode.Episodes.SelectMany(e => accepted.Select(trackId => new EpisodeTrackFile
                {
                    EpisodeId = e.Id,
                    TrackId = trackId,
                    EpisodeFileId = links.SingleOrDefault(l => l.EpisodeId == e.Id && l.TrackId == trackId)?.EpisodeFileId ?? 0
                })).ToList();
                return new ImportDecision(localEpisode);
            }

            if (rejected.Count == 0)
            {
                rejected.Add(new ImportRejection(ImportRejectionReason.DecisionError, "Selected quality profiles are no longer enabled. Choose import targets again."));
            }

            return new ImportDecision(localEpisode, rejected.ToArray());
        }

        private ImportDecision GetDecision(LocalEpisode localEpisode, DownloadClientItem downloadClientItem, bool otherFiles)
        {
            ImportDecision decision = null;

            try
            {
                var fileEpisodeInfo = Parser.Parser.ParsePath(localEpisode.Path);

                localEpisode.FileEpisodeInfo = fileEpisodeInfo;
                localEpisode.Size = _diskProvider.GetFileSize(localEpisode.Path);
                localEpisode.ReleaseType = localEpisode.DownloadClientEpisodeInfo?.ReleaseType ??
                                           localEpisode.FolderEpisodeInfo?.ReleaseType ??
                                           localEpisode.FileEpisodeInfo?.ReleaseType ??
                                           ReleaseType.Unknown;

                _aggregationService.Augment(localEpisode, downloadClientItem);

                if (localEpisode.Episodes.Empty())
                {
                    if (IsPartialSeason(localEpisode))
                    {
                        decision = new ImportDecision(localEpisode, new ImportRejection(ImportRejectionReason.PartialSeason, "Partial season packs are not supported"));
                    }
                    else if (IsSeasonExtra(localEpisode))
                    {
                        decision = new ImportDecision(localEpisode, new ImportRejection(ImportRejectionReason.SeasonExtra, "Extras are not supported"));
                    }
                    else
                    {
                        decision = new ImportDecision(localEpisode, new ImportRejection(ImportRejectionReason.InvalidSeasonOrEpisode, "Invalid season or episode"));
                    }
                }
                else
                {
                    if (downloadClientItem?.DownloadId.IsNotNullOrWhiteSpace() == true)
                    {
                        var trackedDownload = _trackedDownloadService.Find(downloadClientItem.DownloadId);

                        if (trackedDownload?.RemoteEpisode?.Release?.IndexerFlags != null)
                        {
                            localEpisode.IndexerFlags = trackedDownload.RemoteEpisode.Release.IndexerFlags;
                        }
                    }

                    _formatCalculator.UpdateEpisodeCustomFormats(localEpisode);

                    decision = GetDecision(localEpisode, downloadClientItem);
                }
            }
            catch (AugmentingFailedException)
            {
                decision = new ImportDecision(localEpisode, new ImportRejection(ImportRejectionReason.UnableToParse, "Unable to parse file"));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Couldn't import file. {0}", localEpisode.Path);

                decision = new ImportDecision(localEpisode, new ImportRejection(ImportRejectionReason.Error, "Unexpected error processing file"));
            }

            if (decision == null)
            {
                _logger.Error("Unable to make a decision on {0}", localEpisode.Path);
            }
            else if (decision.Rejections.Any())
            {
                _logger.Debug("File rejected for the following reasons: {0}", string.Join(", ", decision.Rejections));
            }
            else
            {
                _logger.Debug("File accepted");
            }

            return decision;
        }

        private ImportRejection EvaluateSpec(IImportDecisionEngineSpecification spec, LocalEpisode localEpisode, DownloadClientItem downloadClientItem)
        {
            try
            {
                var result = spec.IsSatisfiedBy(localEpisode, downloadClientItem);

                if (!result.Accepted)
                {
                    return new ImportRejection(result.Reason, result.Message);
                }
            }
            catch (Exception e)
            {
                // e.Data.Add("report", remoteEpisode.Report.ToJson());
                // e.Data.Add("parsed", remoteEpisode.ParsedEpisodeInfo.ToJson());
                _logger.Error(e, "Couldn't evaluate decision on {0}", localEpisode.Path);
                return new ImportRejection(ImportRejectionReason.DecisionError, $"{spec.GetType().Name}: {e.Message}");
            }

            return null;
        }

        private int GetNonSampleVideoFileCount(List<string> videoFiles, Series series, ParsedEpisodeInfo downloadClientItemInfo, ParsedEpisodeInfo folderInfo)
        {
            var isPossibleSpecialEpisode = downloadClientItemInfo?.IsPossibleSpecialEpisode ?? false;

            // If we might already have a special, don't try to get it from the folder info.
            isPossibleSpecialEpisode = isPossibleSpecialEpisode || (folderInfo?.IsPossibleSpecialEpisode ?? false);

            return videoFiles.Count(file =>
            {
                var sample = _detectSample.IsSample(series, file, isPossibleSpecialEpisode);

                if (sample == DetectSampleResult.Sample)
                {
                    return false;
                }

                return true;
            });
        }

        private bool IsPartialSeason(LocalEpisode localEpisode)
        {
            var downloadClientEpisodeInfo = localEpisode.DownloadClientEpisodeInfo;
            var folderEpisodeInfo = localEpisode.FolderEpisodeInfo;
            var fileEpisodeInfo = localEpisode.FileEpisodeInfo;

            if (downloadClientEpisodeInfo != null && downloadClientEpisodeInfo.IsPartialSeason)
            {
                return true;
            }

            if (folderEpisodeInfo != null && folderEpisodeInfo.IsPartialSeason)
            {
                return true;
            }

            if (fileEpisodeInfo != null && fileEpisodeInfo.IsPartialSeason)
            {
                return true;
            }

            return false;
        }

        private bool IsSeasonExtra(LocalEpisode localEpisode)
        {
            var downloadClientEpisodeInfo = localEpisode.DownloadClientEpisodeInfo;
            var folderEpisodeInfo = localEpisode.FolderEpisodeInfo;
            var fileEpisodeInfo = localEpisode.FileEpisodeInfo;

            if (downloadClientEpisodeInfo != null && downloadClientEpisodeInfo.IsSeasonExtra)
            {
                return true;
            }

            if (folderEpisodeInfo != null && folderEpisodeInfo.IsSeasonExtra)
            {
                return true;
            }

            if (fileEpisodeInfo != null && fileEpisodeInfo.IsSeasonExtra)
            {
                return true;
            }

            return false;
        }
    }
}
