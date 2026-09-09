using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Extras;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.MediaFiles
{
    public interface IRenameEpisodeFileService
    {
        List<RenameEpisodeFilePreview> GetRenamePreviews(int seriesId);
        List<RenameEpisodeFilePreview> GetRenamePreviews(int seriesId, int seasonNumber);
        List<RenameEpisodeFilePreview> GetRenamePreviews(List<int> seriesIds);
    }

    public class RenameEpisodeFileService : IRenameEpisodeFileService,
                                            IExecute<RenameFilesCommand>,
                                            IExecute<RenameSeriesCommand>
    {
        private readonly ISeriesService _seriesService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IMoveEpisodeFiles _episodeFileMover;
        private readonly IEventAggregator _eventAggregator;
        private readonly IEpisodeService _episodeService;
        private readonly IBuildFileNames _filenameBuilder;
        private readonly IDiskProvider _diskProvider;
        private readonly IUpgradeMediaFiles _episodeFileUpgrader;
        private readonly IExtraService _extraService;
        private readonly Logger _logger;

        public RenameEpisodeFileService(ISeriesService seriesService,
                                        IMediaFileService mediaFileService,
                                        IMoveEpisodeFiles episodeFileMover,
                                        IEventAggregator eventAggregator,
                                        IEpisodeService episodeService,
                                        IBuildFileNames filenameBuilder,
                                        IDiskProvider diskProvider,
                                        IUpgradeMediaFiles episodeFileUpgrader,
                                        IExtraService extraService,
                                        Logger logger)
        {
            _seriesService = seriesService;
            _mediaFileService = mediaFileService;
            _episodeFileMover = episodeFileMover;
            _eventAggregator = eventAggregator;
            _episodeService = episodeService;
            _filenameBuilder = filenameBuilder;
            _diskProvider = diskProvider;
            _episodeFileUpgrader = episodeFileUpgrader;
            _extraService = extraService;
            _logger = logger;
        }

        public List<RenameEpisodeFilePreview> GetRenamePreviews(int seriesId)
        {
            var series = _seriesService.GetSeries(seriesId);
            var episodes = _episodeService.GetEpisodeBySeries(seriesId);
            var files = _mediaFileService.GetFilesBySeries(seriesId);

            return GetPreviews(series, episodes, files)
                .OrderByDescending(e => e.SeasonNumber)
                .ThenByDescending(e => e.EpisodeNumbers.First())
                .ToList();
        }

        public List<RenameEpisodeFilePreview> GetRenamePreviews(int seriesId, int seasonNumber)
        {
            var series = _seriesService.GetSeries(seriesId);
            var episodes = _episodeService.GetEpisodesBySeason(seriesId, seasonNumber);
            var files = _mediaFileService.GetFilesBySeason(seriesId, seasonNumber);

            return GetPreviews(series, episodes, files, _mediaFileService.GetFilesBySeries(seriesId))
                .OrderByDescending(e => e.EpisodeNumbers.First()).ToList();
        }

        public List<RenameEpisodeFilePreview> GetRenamePreviews(List<int> seriesIds)
        {
            var seriesList = _seriesService.GetSeries(seriesIds);
            var episodesList = _episodeService.GetEpisodesBySeries(seriesIds).ToLookup(e => e.SeriesId);
            var filesList = _mediaFileService.GetFilesBySeriesIds(seriesIds).ToLookup(f => f.SeriesId);

            return seriesList.SelectMany(series =>
                {
                    var episodes = episodesList[series.Id].ToList();
                    var files = filesList[series.Id].ToList();

                    return GetPreviews(series, episodes, files);
                })
                .OrderByDescending(e => e.SeriesId)
                .ThenByDescending(e => e.SeasonNumber)
                .ThenByDescending(e => e.EpisodeNumbers.First())
                .ToList();
        }

        private IEnumerable<RenameEpisodeFilePreview> GetPreviews(Series series, List<Episode> episodes, List<EpisodeFile> files, List<EpisodeFile> existingFiles = null)
        {
            var previews = new List<RenameEpisodeFilePreview>();
            var plannedStems = new Dictionary<int, string>();
            var resolvedDirectories = new Dictionary<string, string>();
            var filesByStem = (existingFiles ?? files).ToLookup(file => Path.ChangeExtension(MediaFileRecoveryPaths.ResolveFilePath(Path.Combine(series.Path, file.RelativePath), resolvedDirectories), null), StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                var file = f;
                var linkedEpisodeIds = file.TrackFiles?.Value?.Select(l => l.EpisodeId).ToHashSet();
                var episodesInFile = episodes.Where(e => linkedEpisodeIds?.Contains(e.Id) ?? e.EpisodeFileId == file.Id).ToList();
                var episodeFilePath = Path.Combine(series.Path, file.RelativePath);

                if (!episodesInFile.Any())
                {
                    _logger.Warn("File ({0}) is not linked to any episodes", episodeFilePath);
                    continue;
                }

                var seasonNumber = episodesInFile.First().SeasonNumber;
                var newPath = _filenameBuilder.BuildFilePath(episodesInFile, series, file, Path.GetExtension(episodeFilePath));
                var newStem = Path.ChangeExtension(MediaFileRecoveryPaths.ResolveFilePath(newPath, resolvedDirectories), null);
                plannedStems[file.Id] = newStem;

                if (!episodeFilePath.PathEquals(newPath, StringComparison.Ordinal))
                {
                    previews.Add(new RenameEpisodeFilePreview
                    {
                        SeriesId = series.Id,
                        SeasonNumber = seasonNumber,
                        EpisodeNumbers = episodesInFile.Select(e => e.EpisodeNumber).ToList(),
                        EpisodeFileId = file.Id,
                        ExistingPath = file.RelativePath,
                        NewPath = series.Path.GetRelativePath(newPath),
                        Error = filesByStem[newStem].Any(other => other.Id != file.Id) || (!episodeFilePath.PathEquals(newPath) && _diskProvider.FileExists(newPath))
                            ? "Another file uses this base filename. Adjust the episode naming format before retrying."
                            : null
                    });
                }
            }

            var duplicateStems = plannedStems.GroupBy(entry => entry.Value, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var preview in previews.Where(preview => duplicateStems.Contains(plannedStems[preview.EpisodeFileId])))
            {
                preview.Error = "Multiple selected versions would use the same base filename. Include quality or another distinguishing value in the episode naming format.";
            }

            return previews;
        }

        private List<RenamedEpisodeFile> RenameFiles(List<EpisodeFile> episodeFiles, Series series)
        {
            lock (MediaFileOperationLock.ForSeries(series.Id))
            {
                if (episodeFiles.Any(file => file.SeriesId != series.Id))
                {
                    throw new InvalidOperationException("All selected episode files must belong to the requested series.");
                }

                var previews = GetPreviews(series, _episodeService.GetEpisodeBySeries(series.Id), episodeFiles, _mediaFileService.GetFilesBySeries(series.Id)).ToList();
                var conflict = previews.FirstOrDefault(preview => preview.Error != null);
                if (conflict != null)
                {
                    throw new DestinationAlreadyExistsException(conflict.Error);
                }

                return RenameSeriesFiles(episodeFiles, series);
            }
        }

        private List<RenamedEpisodeFile> RenameSeriesFiles(List<EpisodeFile> episodeFiles, Series series)
        {
            var renamed = new List<RenamedEpisodeFile>();

            foreach (var episodeFile in episodeFiles)
            {
                var previousRelativePath = episodeFile.RelativePath;
                var previousPath = Path.Combine(series.Path, episodeFile.RelativePath);
                var committed = false;
                string operationDirectory = null;

                try
                {
                    _logger.Debug("Renaming episode file: {0}", episodeFile);
                    var episodes = _episodeService.GetEpisodesByFileId(episodeFile.Id);
                    var destinationPath = _filenameBuilder.BuildFilePath(episodes, series, episodeFile, Path.GetExtension(episodeFile.RelativePath));
                    operationDirectory = _episodeFileUpgrader.BeginRename(series, episodeFile, destinationPath);
                    _episodeFileMover.MoveEpisodeFile(episodeFile, series);

                    _mediaFileService.Update(episodeFile);
                    committed = true;
                    _extraService.MoveFilesAfterRename(series, episodeFile, true);

                    renamed.Add(new RenamedEpisodeFile
                                {
                                    EpisodeFile = episodeFile,
                                    PreviousRelativePath = previousRelativePath,
                                    PreviousPath = previousPath
                                });

                    _logger.Debug("Renamed episode file: {0}", episodeFile);

                    _eventAggregator.PublishEvent(new EpisodeFileRenamedEvent(series, episodeFile, previousPath));
                    _episodeFileUpgrader.FinishRename(series, operationDirectory);
                }
                catch (DestinationAlreadyExistsException ex)
                {
                    _logger.Warn("File not renamed: {0}", ex.Message);
                }
                catch (FileAlreadyExistsException ex)
                {
                    _logger.Warn("File not renamed, there is already a file at the destination: {0}", ex.Filename);
                }
                catch (SameFilenameException ex)
                {
                    _logger.Debug("File not renamed, source and destination are the same: {0}", ex.Filename);
                }
                catch (Exception ex)
                {
                    if (!committed)
                    {
                        _episodeFileUpgrader.RecoverFileOperations(series);
                    }

                    _logger.Error(ex, "Failed to rename file {0}", previousPath);
                }
            }

            if (renamed.Any())
            {
                _diskProvider.RemoveEmptySubfolders(series.Path);

                _eventAggregator.PublishEvent(new SeriesRenamedEvent(series, renamed));
            }

            return renamed;
        }

        public void Execute(RenameFilesCommand message)
        {
            var series = _seriesService.GetSeries(message.SeriesId);
            _episodeFileUpgrader.RecoverImports(series);
            using var operation = MediaFileOperationLock.Acquire(new[] { message.SeriesId });
            series = _seriesService.GetSeries(message.SeriesId);
            _episodeFileUpgrader.RecoverFileOperations(series);
            var episodeFiles = _mediaFileService.Get(message.Files);

            _logger.ProgressInfo("Renaming {0} files for {1}", episodeFiles.Count, series.Title);
            var renamedFiles = RenameFiles(episodeFiles, series);
            _logger.ProgressInfo("{0} selected episode files renamed for {1}", renamedFiles.Count, series.Title);

            _eventAggregator.PublishEvent(new RenameCompletedEvent());
        }

        public void Execute(RenameSeriesCommand message)
        {
            _logger.Debug("Renaming all files for selected series");
            var seriesToRename = _seriesService.GetSeries(message.SeriesIds);

            foreach (var series in seriesToRename)
            {
                _episodeFileUpgrader.RecoverImports(series);
                using var operation = MediaFileOperationLock.Acquire(new[] { series.Id });
                var episodeFiles = _mediaFileService.GetFilesBySeries(series.Id);
                var currentSeries = _seriesService.GetSeries(series.Id);
                _episodeFileUpgrader.RecoverFileOperations(currentSeries);
                _logger.ProgressInfo("Renaming all files in series: {0}", currentSeries.Title);
                var renamedFiles = RenameFiles(episodeFiles, currentSeries);
                _logger.ProgressInfo("{0} episode files renamed for {1}", renamedFiles.Count, currentSeries.Title);
            }

            _eventAggregator.PublishEvent(new RenameCompletedEvent());
        }
    }
}
