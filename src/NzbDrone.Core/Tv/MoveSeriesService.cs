using System;
using System.Collections.Generic;
using System.IO;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Extras;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Tv.Commands;
using NzbDrone.Core.Tv.Events;

namespace NzbDrone.Core.Tv
{
    public class MoveSeriesService : IExecute<MoveSeriesCommand>, IExecute<BulkMoveSeriesCommand>
    {
        private readonly ISeriesService _seriesService;
        private readonly IBuildFileNames _filenameBuilder;
        private readonly IDiskProvider _diskProvider;
        private readonly ISeriesFolderMoveService _folderMoveService;
        private readonly IUpgradeMediaFiles _upgradeMediaFiles;
        private readonly IExtraService _extraService;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public MoveSeriesService(ISeriesService seriesService,
                                 IBuildFileNames filenameBuilder,
                                 IDiskProvider diskProvider,
                                 ISeriesFolderMoveService folderMoveService,
                                 IUpgradeMediaFiles upgradeMediaFiles,
                                 IExtraService extraService,
                                 IEventAggregator eventAggregator,
                                 Logger logger)
        {
            _seriesService = seriesService;
            _filenameBuilder = filenameBuilder;
            _diskProvider = diskProvider;
            _folderMoveService = folderMoveService;
            _upgradeMediaFiles = upgradeMediaFiles;
            _extraService = extraService;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        private void MoveSingleSeries(Series series, string sourcePath, string destinationPath, int? index = null, int? total = null)
        {
            if (!sourcePath.IsPathValid(PathValidationType.CurrentOs))
            {
                _logger.Warn("Folder '{0}' for '{1}' is invalid, unable to move series. Try moving files manually", sourcePath, series.Title);
                return;
            }

            if (index != null && total != null)
            {
                _logger.ProgressInfo("Moving {0} from '{1}' to '{2}' ({3}/{4})", series.Title, sourcePath, destinationPath, index + 1, total);
            }
            else
            {
                _logger.ProgressInfo("Moving {0} from '{1}' to '{2}'", series.Title, sourcePath, destinationPath);
            }

            if (sourcePath.PathEquals(destinationPath))
            {
                _logger.ProgressInfo("{0} is already in the specified location '{1}'.", series, destinationPath);
                return;
            }

            using var operationLock = MediaFileOperationLock.AcquireAll();
            try
            {
                _folderMoveService.Recover(series);

                // Commands queued before path deferral may already contain the requested database path.
                if (!series.Path.PathEquals(sourcePath) && series.Path.PathEquals(destinationPath))
                {
                    if (!_diskProvider.FolderExists(sourcePath) && _diskProvider.FolderExists(destinationPath))
                    {
                        return;
                    }

                    if (_diskProvider.FolderExists(sourcePath))
                    {
                        series.Path = sourcePath;
                        _seriesService.UpdateSeries(series, false);
                    }
                }

                if (!series.Path.PathEquals(sourcePath))
                {
                    throw new IOException("Series folder changed since this move was requested. Request the move again.");
                }

                _upgradeMediaFiles.RecoverImports(series);
                _extraService.EnsureRenamesCompleted(series);
                _folderMoveService.Move(series, sourcePath, destinationPath);
                _logger.ProgressInfo("{0} moved successfully to {1}", series.Title, destinationPath);
                _eventAggregator.PublishEvent(new SeriesMovedEvent(series, sourcePath, destinationPath));
            }
            catch (IOException ex)
            {
                _logger.Error(ex, "Unable to move series from '{0}' to '{1}'. Its files and recovery information were preserved", sourcePath, destinationPath);
                throw;
            }
        }

        public void Execute(MoveSeriesCommand message)
        {
            var series = _seriesService.GetSeries(message.SeriesId);
            MoveSingleSeries(series, message.SourcePath, message.DestinationPath);
        }

        public void Execute(BulkMoveSeriesCommand message)
        {
            var seriesToMove = message.Series;
            var destinationRootFolder = message.DestinationRootFolder;

            _logger.ProgressInfo("Moving {0} series to '{1}'", seriesToMove.Count, destinationRootFolder);

            var failures = new List<Exception>();
            for (var index = 0; index < seriesToMove.Count; index++)
            {
                var s = seriesToMove[index];
                var series = _seriesService.GetSeries(s.SeriesId);
                var destinationPath = Path.Combine(destinationRootFolder, _filenameBuilder.GetSeriesFolder(series));

                try
                {
                    MoveSingleSeries(series, s.SourcePath, destinationPath, index, seriesToMove.Count);
                }
                catch (IOException ex)
                {
                    failures.Add(ex);
                }
            }

            if (failures.Count > 0)
            {
                throw new AggregateException("One or more series folders could not be moved.", failures);
            }

            _logger.ProgressInfo("Finished moving {0} series to '{1}'", seriesToMove.Count, destinationRootFolder);
        }
    }
}
