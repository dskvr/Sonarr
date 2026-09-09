using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Core.Extras;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.MediaFiles.EpisodeImport
{
    public interface IImportApprovedEpisodes
    {
        List<ImportResult> Import(List<ImportDecision> decisions, bool newDownload, DownloadClientItem downloadClientItem = null, ImportMode importMode = ImportMode.Auto);
    }

    public class ImportApprovedEpisodes : IImportApprovedEpisodes
    {
        private readonly IUpgradeMediaFiles _episodeFileUpgrader;
        private readonly IMediaFileService _mediaFileService;
        private readonly IExtraService _extraService;
        private readonly IExistingExtraFiles _existingExtraFiles;
        private readonly IDiskProvider _diskProvider;
        private readonly IHistoryService _historyService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IEpisodeTrackFileService _trackFileService;
        private readonly ISeriesQualityTrackService _qualityTrackService;
        private readonly Logger _logger;

        public ImportApprovedEpisodes(IUpgradeMediaFiles episodeFileUpgrader,
                                      IMediaFileService mediaFileService,
                                      IExtraService extraService,
                                      IExistingExtraFiles existingExtraFiles,
                                      IDiskProvider diskProvider,
                                      IHistoryService historyService,
                                      IEventAggregator eventAggregator,
                                      IManageCommandQueue commandQueueManager,
                                      IEpisodeTrackFileService trackFileService,
                                      ISeriesQualityTrackService qualityTrackService,
                                      Logger logger)
        {
            _episodeFileUpgrader = episodeFileUpgrader;
            _mediaFileService = mediaFileService;
            _extraService = extraService;
            _existingExtraFiles = existingExtraFiles;
            _diskProvider = diskProvider;
            _historyService = historyService;
            _eventAggregator = eventAggregator;
            _commandQueueManager = commandQueueManager;
            _trackFileService = trackFileService;
            _qualityTrackService = qualityTrackService;
            _logger = logger;
        }

        public List<ImportResult> Import(List<ImportDecision> decisions, bool newDownload, DownloadClientItem downloadClientItem = null, ImportMode importMode = ImportMode.Auto)
        {
            var qualifiedImports = decisions
                .Where(decision => decision.Approved)
                .GroupBy(decision => decision.LocalEpisode.Series.Id)
                .SelectMany(group => group
                    .OrderByDescending(decision => decision.LocalEpisode.Quality, new QualityModelComparer(group.First().LocalEpisode.Series.QualityProfile))
                    .ThenByDescending(decision => decision.LocalEpisode.Size))
                .ToList();

            var importResults = new List<ImportResult>();

            foreach (var importDecision in qualifiedImports.OrderBy(e => e.LocalEpisode.Episodes.Select(episode => episode.EpisodeNumber).MinOrDefault())
                                                           .ThenByDescending(e => e.LocalEpisode.Size))
            {
                var localEpisode = importDecision.LocalEpisode;
                _episodeFileUpgrader.RecoverImports(localEpisode.Series);
                using var operation = MediaFileOperationLock.Acquire(new[] { localEpisode.Series.Id });
                _episodeFileUpgrader.RecoverFileOperations(localEpisode.Series);
                var oldFiles = new List<DeletedEpisodeFile>();
                var imported = false;

                try
                {
                    // check if already imported
                    var enabledTracks = _qualityTrackService.GetEnabledTracks(localEpisode.Series.Id);
                    if (localEpisode.TargetQualityTrackIds == null && !newDownload)
                    {
                        var knownFiles = _mediaFileService.GetFilesWithRelativePath(localEpisode.Series.Id, localEpisode.Series.Path.GetRelativePath(localEpisode.Path));
                        var knownTargets = knownFiles.SelectMany(f => _trackFileService.GetForFile(f.Id)).Select(l => l.TrackId).Distinct().ToList();
                        if (knownTargets.Count > 0)
                        {
                            localEpisode.TargetQualityTrackIds = knownTargets;
                        }
                    }

                    localEpisode.TargetQualityTrackIds ??= enabledTracks.Where(t => t.IsPrimary).Select(t => t.Id).ToList();
                    if (localEpisode.TargetQualityTrackIds.Count == 0)
                    {
                        importResults.Add(new ImportResult(importDecision, "Choose an enabled quality profile for this import."));
                        continue;
                    }

                    var importedTargets = importResults.Where(r => r.Result == ImportResultType.Imported &&
                            r.ImportDecision.LocalEpisode.Episodes.Select(e => e.Id).Intersect(localEpisode.Episodes.Select(e => e.Id)).Any())
                        .SelectMany(r => r.ImportDecision.LocalEpisode.TargetQualityTrackIds).ToHashSet();
                    var remainingTargets = localEpisode.TargetQualityTrackIds.Where(id => !importedTargets.Contains(id)).ToList();
                    if (remainingTargets.Count == 0)
                    {
                        importResults.Add(new ImportResult(importDecision, "Episode has already been imported"));
                        continue;
                    }

                    localEpisode.TargetQualityTrackIds = remainingTargets;

                    var episodeFile = localEpisode.ToEpisodeFile();
                    episodeFile.Size = _diskProvider.GetFileSize(localEpisode.Path);

                    if (downloadClientItem?.DownloadId.IsNotNullOrWhiteSpace() == true)
                    {
                        var grabHistory = _historyService.FindByDownloadId(downloadClientItem.DownloadId)
                            .OrderByDescending(h => h.Date)
                            .FirstOrDefault(h => h.EventType == EpisodeHistoryEventType.Grabbed);

                        if (Enum.TryParse(grabHistory?.Data.GetValueOrDefault("indexerFlags"), true, out IndexerFlags flags))
                        {
                            episodeFile.IndexerFlags = flags;
                        }

                        // Prefer the release type from the grabbed history
                        if (Enum.TryParse(grabHistory?.Data.GetValueOrDefault("releaseType"), true, out ReleaseType releaseType) &&
                            releaseType != ReleaseType.Unknown)
                        {
                            episodeFile.ReleaseType = releaseType;
                        }
                    }

                    bool copyOnly;
                    switch (importMode)
                    {
                        default:
                        case ImportMode.Auto:
                            copyOnly = downloadClientItem is { CanMoveFiles: false };
                            break;
                        case ImportMode.Move:
                            copyOnly = false;
                            break;
                        case ImportMode.Copy:
                            copyOnly = true;
                            break;
                    }

                    if (newDownload)
                    {
                        if (downloadClientItem is { OutputPath.IsEmpty: false })
                        {
                            var outputDirectory = downloadClientItem.OutputPath.Directory.ToString();

                            if (outputDirectory.IsParentPath(localEpisode.Path))
                            {
                                episodeFile.OriginalFilePath = outputDirectory.GetRelativePath(localEpisode.Path);
                            }
                        }

                        var moveResult = _episodeFileUpgrader.UpgradeEpisodeFile(episodeFile, localEpisode, copyOnly);
                        oldFiles = moveResult.OldFiles;
                    }
                    else
                    {
                        var previousFiles = _mediaFileService.GetFilesWithRelativePath(localEpisode.Series.Id, episodeFile.RelativePath);
                        if (previousFiles.Count > 1)
                        {
                            throw new InvalidOperationException("Multiple records use this path. Resolve file ownership before importing.");
                        }

                        var links = localEpisode.Episodes.SelectMany(e => localEpisode.TargetQualityTrackIds.Select(trackId => new EpisodeTrackFile
                        {
                            EpisodeId = e.Id,
                            TrackId = trackId
                        })).ToList();

                        if (previousFiles.Count == 1)
                        {
                            episodeFile.Id = previousFiles[0].Id;
                            _trackFileService.UpdateFile(episodeFile, links, true);
                        }
                        else
                        {
                            _trackFileService.ImportFile(episodeFile, links);
                        }
                    }

                    importResults.Add(new ImportResult(importDecision, episodeFile));
                    imported = true;

                    try
                    {
                        _eventAggregator.PublishEvent(new EpisodeFileAddedEvent(episodeFile));

                        if (newDownload)
                        {
                            if (localEpisode.ScriptImported)
                            {
                                _existingExtraFiles.ImportExtraFiles(localEpisode.Series, localEpisode.PossibleExtraFiles, localEpisode.FileNameBeforeRename, episodeFile.Id);

                                if (localEpisode.FileNameBeforeRename != episodeFile.RelativePath)
                                {
                                    _extraService.MoveFilesAfterRename(localEpisode.Series, episodeFile);
                                }
                            }

                            if (!localEpisode.ScriptImported || localEpisode.ShouldImportExtras)
                            {
                                _extraService.ImportEpisode(localEpisode, episodeFile, copyOnly);
                            }
                        }
                    }
                    finally
                    {
                        // File ownership is committed even if post-processing fails.
                        _eventAggregator.PublishEvent(new EpisodeImportedEvent(localEpisode, episodeFile, oldFiles, newDownload, downloadClientItem));
                    }
                }
                catch (Exception e) when (imported)
                {
                    _logger.Warn(e, "Episode file imported, but a post-import action failed: {0}", localEpisode);
                }
                catch (RootFolderNotFoundException e)
                {
                    _logger.Warn(e, "Couldn't import episode " + localEpisode);
                    _eventAggregator.PublishEvent(new EpisodeImportFailedEvent(e, localEpisode, newDownload, downloadClientItem));

                    importResults.Add(new ImportResult(importDecision, "Failed to import episode, Root folder missing."));
                }
                catch (DestinationAlreadyExistsException e)
                {
                    _logger.Warn(e, "Couldn't import episode " + localEpisode);
                    importResults.Add(new ImportResult(importDecision, "Failed to import episode, Destination already exists."));

                    _commandQueueManager.Push(new RescanSeriesCommand(localEpisode.Series.Id));
                }
                catch (RecycleBinException e)
                {
                    _logger.Warn(e, "Couldn't import episode " + localEpisode);
                    _eventAggregator.PublishEvent(new EpisodeImportFailedEvent(e, localEpisode, newDownload, downloadClientItem));

                    importResults.Add(new ImportResult(importDecision, "Failed to import episode, unable to move existing file to the Recycle Bin."));
                }
                catch (Exception e)
                {
                    _logger.Warn(e, "Couldn't import episode " + localEpisode);
                    importResults.Add(new ImportResult(importDecision, "Failed to import episode"));
                }
            }

            // Adding all the rejected decisions
            importResults.AddRange(decisions.Where(c => !c.Approved)
                                            .Select(d => new ImportResult(d, d.Rejections.Select(r => r.Message).ToArray())));

            return importResults;
        }
    }
}
