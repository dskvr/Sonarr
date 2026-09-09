using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Extras;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.MediaFiles
{
    public interface IUpgradeMediaFiles
    {
        EpisodeFileMoveResult UpgradeEpisodeFile(EpisodeFile episodeFile, LocalEpisode localEpisode, bool copyOnly = false);
        string BeginRename(Series series, EpisodeFile file, string destinationPath);
        void FinishRename(Series series, string operationDirectory);
        void RecoverImports(Series series);
        void RecoverFileOperations(Series series);
    }

    public class UpgradeMediaFileService : IUpgradeMediaFiles
    {
        private const string StagingFolder = ".sonarr-import";
        private readonly IRecycleBinProvider _recycleBinProvider;
        private readonly IMediaFileService _mediaFileService;
        private readonly IMoveEpisodeFiles _episodeFileMover;
        private readonly IDiskProvider _diskProvider;
        private readonly IDiskTransferService _diskTransferService;
        private readonly IEpisodeTrackFileService _trackFileService;
        private readonly ISeriesQualityTrackService _qualityTrackService;
        private readonly IBuildFileNames _fileNameBuilder;
        private readonly IConfigService _configService;
        private readonly IEpisodeService _episodeService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IAppFolderInfo _appFolderInfo;
        private readonly ISeriesFolderMoveService _seriesFolderMoveService;
        private readonly ISeriesService _seriesService;
        private readonly IExtraService _extraService;
        private readonly Logger _logger;

        public UpgradeMediaFileService(IRecycleBinProvider recycleBinProvider,
                                       IMediaFileService mediaFileService,
                                       IMoveEpisodeFiles episodeFileMover,
                                       IDiskProvider diskProvider,
                                       IDiskTransferService diskTransferService,
                                       IEpisodeTrackFileService trackFileService,
                                       ISeriesQualityTrackService qualityTrackService,
                                       IBuildFileNames fileNameBuilder,
                                       IConfigService configService,
                                       IEpisodeService episodeService,
                                       IEventAggregator eventAggregator,
                                       IAppFolderInfo appFolderInfo,
                                       ISeriesFolderMoveService seriesFolderMoveService,
                                       ISeriesService seriesService,
                                       IExtraService extraService,
                                       Logger logger)
        {
            _recycleBinProvider = recycleBinProvider;
            _mediaFileService = mediaFileService;
            _episodeFileMover = episodeFileMover;
            _diskProvider = diskProvider;
            _diskTransferService = diskTransferService;
            _trackFileService = trackFileService;
            _qualityTrackService = qualityTrackService;
            _fileNameBuilder = fileNameBuilder;
            _configService = configService;
            _episodeService = episodeService;
            _eventAggregator = eventAggregator;
            _appFolderInfo = appFolderInfo;
            _seriesFolderMoveService = seriesFolderMoveService;
            _seriesService = seriesService;
            _extraService = extraService;
            _logger = logger;
        }

        public EpisodeFileMoveResult UpgradeEpisodeFile(EpisodeFile episodeFile, LocalEpisode localEpisode, bool copyOnly = false)
        {
            RecoverImports(localEpisode.Series);
            lock (MediaFileOperationLock.ForSeries(localEpisode.Series.Id))
            {
                RecoverFileOperations(localEpisode.Series);
                return Import(episodeFile, localEpisode, copyOnly);
            }
        }

        public string BeginRename(Series series, EpisodeFile file, string destinationPath)
        {
            RecoverImports(series);
            lock (MediaFileOperationLock.ForSeries(series.Id))
            {
                RecoverFileOperations(series);
                var current = _mediaFileService.Get(new[] { file.Id }).SingleOrDefault();
                if (file.SeriesId != series.Id || current == null || current.SeriesId != series.Id || current.RelativePath != file.RelativePath)
                {
                    throw new InvalidOperationException("Episode file changed before rename. Refresh the file selection and retry.");
                }

                var sourcePath = ValidateRecoveryPath(series.Path, Path.Combine(series.Path, current.RelativePath), true);
                destinationPath = ValidateRecoveryPath(series.Path, destinationPath, true);
                if (sourcePath.PathEquals(destinationPath, StringComparison.Ordinal))
                {
                    return null;
                }

                if (!_diskProvider.FileExists(sourcePath))
                {
                    throw new FileNotFoundException("Episode file is missing before rename.", sourcePath);
                }

                var resolvedDirectories = new Dictionary<string, string>();
                var resolvedDestination = MediaFileRecoveryPaths.ResolveFilePath(destinationPath, resolvedDirectories);
                if (_mediaFileService.GetFilesBySeries(series.Id).Any(other => other.Id != file.Id &&
                    HasSameStem(MediaFileRecoveryPaths.ResolveFilePath(Path.Combine(series.Path, other.RelativePath), resolvedDirectories), resolvedDestination)) ||
                    (!sourcePath.PathEquals(destinationPath) && _diskProvider.FileExists(destinationPath)))
                {
                    throw new DestinationAlreadyExistsException("Destination is used by another file. Adjust the episode naming format before retrying.");
                }

                if (sourcePath.PathEquals(destinationPath, StringComparison.OrdinalIgnoreCase) && _diskProvider.FileExists(sourcePath + ".backup~"))
                {
                    throw new DestinationAlreadyExistsException("An intermediate file already exists for this case-only rename. Resolve it before retrying.");
                }

                var recoveryRoot = GetRecoveryRoot(series.Id);
                var operationDirectory = Path.Combine(recoveryRoot, Guid.NewGuid().ToString("N"));
                _diskProvider.CreateFolder(operationDirectory);
                WriteJournal(Path.Combine(operationDirectory, "rename.json"), new RenameJournal
                {
                    SeriesId = series.Id,
                    EpisodeFileId = file.Id,
                    SourceRelativePath = series.Path.GetRelativePath(sourcePath),
                    DestinationRelativePath = series.Path.GetRelativePath(destinationPath),
                    ResolvedSourcePath = MediaFileRecoveryPaths.ResolveFilePath(sourcePath),
                    ResolvedDestinationPath = MediaFileRecoveryPaths.ResolveFilePath(destinationPath)
                });
                return operationDirectory;
            }
        }

        public void FinishRename(Series series, string operationDirectory)
        {
            if (operationDirectory == null)
            {
                return;
            }

            lock (MediaFileOperationLock.ForSeries(series.Id))
            {
                var recoveryRoot = GetRecoveryRoot(series.Id);
                operationDirectory = ValidateRecoveryPath(recoveryRoot, operationDirectory);
                CleanupReceiptDirectory(operationDirectory);
            }
        }

        private EpisodeFileMoveResult Import(EpisodeFile episodeFile, LocalEpisode localEpisode, bool copyOnly)
        {
            var series = localEpisode.Series;
            var enabledTracks = _qualityTrackService.GetEnabledTracks(series.Id);
            var targets = localEpisode.TargetQualityTrackIds ?? enabledTracks.Where(t => t.IsPrimary).Select(t => t.Id).ToList();

            if (targets.Count == 0 || targets.Any(id => enabledTracks.All(t => t.Id != id)))
            {
                throw new InvalidOperationException("Selected quality profiles are no longer enabled. Choose import targets again.");
            }

            localEpisode.TargetQualityTrackIds = targets.Distinct().ToList();
            if (!localEpisode.ManualImport && !localEpisode.LegacyQualityTrackTarget && localEpisode.TargetQualityTrackSignatures != null)
            {
                foreach (var track in enabledTracks.Where(t => targets.Contains(t.Id)))
                {
                    var profile = track.QualityProfile.Value;
                    if (!localEpisode.TargetQualityTrackSignatures.TryGetValue(track.Id, out var signature) || signature != QualityTrackSnapshot.ProfileSignature(profile))
                    {
                        throw new InvalidOperationException("Quality profile changed after import validation. Retry the import to validate the current profile.");
                    }
                }
            }

            var episodeIds = localEpisode.Episodes.Select(e => e.Id).ToHashSet();
            var links = _trackFileService.GetForSeries(series.Id);
            if (!localEpisode.ManualImport && localEpisode.ExpectedTrackFiles != null &&
                localEpisode.ExpectedTrackFiles.Where(l => targets.Contains(l.TrackId) && episodeIds.Contains(l.EpisodeId)).Any(expected =>
                    (links.SingleOrDefault(l => l.EpisodeId == expected.EpisodeId && l.TrackId == expected.TrackId)?.EpisodeFileId ?? 0) != expected.EpisodeFileId))
            {
                throw new InvalidOperationException("Episode versions changed after import validation. Retry the import to compare against the current files.");
            }

            var replacedLinks = links.Where(l => episodeIds.Contains(l.EpisodeId) && targets.Contains(l.TrackId)).ToList();
            var oldFileIds = replacedLinks.Select(l => l.EpisodeFileId).Distinct().ToList();
            localEpisode.IsUpgrade = oldFileIds.Count > 0;
            var existingFiles = _mediaFileService.Get(oldFileIds);
            var destination = _fileNameBuilder.BuildFilePath(localEpisode.Episodes, series, episodeFile, Path.GetExtension(localEpisode.Path), null, localEpisode.CustomFormats);
            var rootFolder = _diskProvider.GetParentFolder(series.Path);

            if (!_diskProvider.FolderExists(rootFolder))
            {
                throw new RootFolderNotFoundException($"Root folder '{rootFolder}' was not found.");
            }

            destination = ValidateRecoveryPath(series.Path, destination, true);

            var resolvedDirectories = new Dictionary<string, string>();
            var resolvedDestination = MediaFileRecoveryPaths.ResolveFilePath(destination, resolvedDirectories);
            var seriesFiles = _mediaFileService.GetFilesBySeries(series.Id);
            var collisions = seriesFiles
                .Where(f => MediaFileRecoveryPaths.ResolveFilePath(Path.Combine(series.Path, f.RelativePath), resolvedDirectories)
                    .PathEquals(resolvedDestination, StringComparison.OrdinalIgnoreCase)).ToList();

            if (collisions.Count > 1 || collisions.Any(f => !oldFileIds.Contains(f.Id) || links.Any(l => l.EpisodeFileId == f.Id && !replacedLinks.Contains(l))) ||
                (collisions.Count == 0 && _diskProvider.FileExists(destination)))
            {
                throw new DestinationAlreadyExistsException("Destination is used by another version. Include quality or a distinguishing release value in the episode naming format.");
            }

            if (seriesFiles.Any(file => HasSameStem(MediaFileRecoveryPaths.ResolveFilePath(Path.Combine(series.Path, file.RelativePath), resolvedDirectories), resolvedDestination) &&
                (!oldFileIds.Contains(file.Id) || links.Any(link => link.EpisodeFileId == file.Id && !replacedLinks.Contains(link)))))
            {
                throw new DestinationAlreadyExistsException("Another retained version uses the same base filename. Use a distinct filename to keep subtitles and metadata separate.");
            }

            var operationId = Guid.NewGuid().ToString("N");
            var workFolder = Path.Combine(series.Path, StagingFolder, operationId);
            var operationDirectory = Path.Combine(GetRecoveryRoot(series.Id), operationId);
            var resolvedWorkDirectory = Path.GetDirectoryName(MediaFileRecoveryPaths.ResolveFilePath(Path.Combine(workFolder, ".receipt")));
            var journalPath = Path.Combine(operationDirectory, "import.json");
            var journal = new ImportJournal
            {
                SeriesId = series.Id,
                SourcePath = localEpisode.Path,
                ResolvedSourcePath = MediaFileRecoveryPaths.ResolveFilePath(localEpisode.Path),
                WorkDirectory = workFolder,
                ResolvedWorkDirectory = resolvedWorkDirectory,
                DestinationPath = destination,
                ResolvedDestinationPath = MediaFileRecoveryPaths.ResolveFilePath(destination),
                StagedPath = Path.Combine(workFolder, "incoming", Path.GetFileName(localEpisode.Path)),
                ResolvedStagedPath = MediaFileRecoveryPaths.ResolveFilePath(Path.Combine(workFolder, "incoming", Path.GetFileName(localEpisode.Path))),
                BackupPath = collisions.Count > 0 ? Path.Combine(workFolder, "previous", Path.GetFileName(destination)) : null,
                ResolvedBackupPath = collisions.Count > 0 ? Path.Combine(resolvedWorkDirectory, "previous", Path.GetFileName(destination)) : null,
                BackupOriginalPath = collisions.Count > 0 ? Path.Combine(series.Path, collisions[0].RelativePath) : null,
                ResolvedBackupOriginalPath = collisions.Count > 0 ? MediaFileRecoveryPaths.ResolveFilePath(Path.Combine(series.Path, collisions[0].RelativePath)) : null,
                DestinationExisted = collisions.Count > 0 && _diskProvider.FileExists(Path.Combine(series.Path, collisions[0].RelativePath)),
                BackupSize = collisions.Count > 0 && _diskProvider.FileExists(Path.Combine(series.Path, collisions[0].RelativePath))
                    ? _diskProvider.GetFileSize(Path.Combine(series.Path, collisions[0].RelativePath))
                    : 0,
                OldFileIds = oldFileIds,
                OldFiles = existingFiles.Select(file => SnapshotFile(file, series.Path)).ToList(),
                OldLinks = links.Where(l => oldFileIds.Contains(l.EpisodeFileId)).Select(l => new EpisodeTrackFile
                {
                    EpisodeId = l.EpisodeId,
                    TrackId = l.TrackId,
                    EpisodeFileId = l.EpisodeFileId
                }).ToList(),
                EpisodeIds = episodeIds.ToList(),
                TrackIds = targets,
                CopyOnly = copyOnly,
                DateAdded = episodeFile.DateAdded
            };
            var result = new EpisodeFileMoveResult();
            localEpisode.ResolvedImportSourcePath = journal.ResolvedSourcePath;
            var committed = false;
            var journalWritten = false;
            var originalPath = episodeFile.Path;

            ValidateRecoveryPath(series.Path, Path.GetDirectoryName(workFolder));
            _diskProvider.CreateFolder(operationDirectory);
            _diskProvider.CreateFolder(resolvedWorkDirectory);
            _diskProvider.CreateFolder(Path.GetDirectoryName(journal.ResolvedStagedPath));
            if (journal.BackupPath != null)
            {
                _diskProvider.CreateFolder(Path.GetDirectoryName(journal.ResolvedBackupPath));
            }

            try
            {
                // Preserve the download and old library bytes until the association transaction commits.
                MediaFileRecoveryPaths.VerifyResolvedPath(journal.SourcePath, journal.ResolvedSourcePath);
                _diskTransferService.TransferFile(
                    journal.ResolvedSourcePath,
                    journal.ResolvedStagedPath,
                    _configService.CopyUsingHardlinks ? TransferMode.HardLinkOrCopy : TransferMode.Copy);

                if (!_diskProvider.FileExists(journal.ResolvedStagedPath) || _diskProvider.GetFileSize(journal.ResolvedStagedPath) != episodeFile.Size)
                {
                    throw new IOException("Staged episode file did not match the source size.");
                }

                MediaFileRecoveryPaths.VerifyResolvedPath(destination, journal.ResolvedDestinationPath);
                MediaFileRecoveryPaths.VerifyResolvedPath(journal.SourcePath, journal.ResolvedSourcePath);

                WriteJournal(journalPath, journal);
                journalWritten = true;

                if (journal.BackupPath != null && _diskProvider.FileExists(journal.BackupOriginalPath))
                {
                    MediaFileRecoveryPaths.VerifyResolvedPath(journal.BackupOriginalPath, journal.ResolvedBackupOriginalPath);
                    _diskTransferService.TransferFile(journal.ResolvedBackupOriginalPath, journal.ResolvedBackupPath, TransferMode.Copy);
                    if (_diskProvider.GetFileSize(journal.ResolvedBackupPath) != journal.BackupSize)
                    {
                        throw new IOException("Episode backup did not match the original file size.");
                    }

                    journal.BackupReady = true;
                    WriteJournal(journalPath, journal);
                    _diskProvider.DeleteFile(journal.ResolvedBackupOriginalPath);
                }

                MediaFileRecoveryPaths.VerifyResolvedPath(destination, journal.ResolvedDestinationPath);
                MediaFileRecoveryPaths.VerifyResolvedPath(journal.SourcePath, journal.ResolvedSourcePath);
                episodeFile.Path = journal.ResolvedStagedPath;
                localEpisode.ResolvedImportDestinationPath = journal.ResolvedDestinationPath;
                localEpisode.OldFiles = existingFiles.Select(f => new DeletedEpisodeFile(f, null)).ToList();
                if (copyOnly)
                {
                    _episodeFileMover.CopyEpisodeFile(episodeFile, localEpisode);
                }
                else
                {
                    _episodeFileMover.MoveEpisodeFile(episodeFile, localEpisode);
                }

                var importedPath = Path.Combine(series.Path, episodeFile.RelativePath);
                if (!importedPath.PathEquals(destination))
                {
                    importedPath = ValidateRecoveryPath(series.Path, importedPath, true);
                    if (_mediaFileService.GetFilesBySeries(series.Id)
                        .Any(f => Path.Combine(series.Path, f.RelativePath).PathEquals(importedPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new DestinationAlreadyExistsException("Import script returned a destination used by another episode file.");
                    }

                    destination = importedPath;
                    journal.DestinationPath = importedPath;
                    journal.ResolvedDestinationPath = MediaFileRecoveryPaths.ResolveFilePath(importedPath);
                    WriteJournal(journalPath, journal);
                }

                MediaFileRecoveryPaths.VerifyResolvedPath(destination, journal.ResolvedDestinationPath);
                MediaFileRecoveryPaths.VerifyResolvedPath(journal.SourcePath, journal.ResolvedSourcePath);

                if (!_diskProvider.FileExists(destination))
                {
                    throw new IOException("Imported episode file is missing at the destination.");
                }

                episodeFile.Size = _diskProvider.GetFileSize(destination);
                _trackFileService.ImportFile(episodeFile, localEpisode.Episodes.SelectMany(e => targets.Select(trackId => new EpisodeTrackFile
                {
                    EpisodeId = e.Id,
                    TrackId = trackId
                })).ToList());
                committed = true;
                result.EpisodeFile = episodeFile;
                journal.CommittedFileId = episodeFile.Id;
                WriteJournal(journalPath, journal);
                CompleteImport(series, journal, result.OldFiles);
                CleanupImportWork(journal);
                CleanupReceiptDirectory(operationDirectory);
            }
            catch (Exception ex) when (committed)
            {
                // The new file is already authoritative. Leave the receipt so the next scan retries cleanup.
                _logger.Warn(ex, "Episode imported; old-file cleanup will be retried on the next scan: {0}", destination);
            }
            catch
            {
                if (journalWritten)
                {
                    RollbackImport(journal);
                }

                CleanupImportWork(journal);
                CleanupReceiptDirectory(operationDirectory);
                throw;
            }
            finally
            {
                if (!localEpisode.ScriptImported)
                {
                    episodeFile.Path = originalPath;
                }
            }

            localEpisode.OldFiles = result.OldFiles;
            return result;
        }

        public void RecoverImports(Series series)
        {
            // Nested imports/scans share an outer series operation that already recovered its folder.
            // Global folder recovery must never upgrade an already-held series stripe to all stripes.
            if (Monitor.IsEntered(MediaFileOperationLock.ForSeries(series.Id)))
            {
                RecoverFileOperations(series);
                return;
            }

            while (true)
            {
                _seriesFolderMoveService.Recover(series);
                lock (MediaFileOperationLock.ForSeries(series.Id))
                {
                    if (_seriesFolderMoveService.HasPendingMove(series.Id))
                    {
                        continue;
                    }

                    RecoverFileOperations(series);
                    return;
                }
            }
        }

        public void RecoverFileOperations(Series series)
        {
            lock (MediaFileOperationLock.ForSeries(series.Id))
            {
                if (_seriesFolderMoveService.HasPendingMove(series.Id))
                {
                    throw new IOException("Series folder recovery is pending. Retry this operation.");
                }

                series.Path = _seriesService.GetSeries(series.Id).Path;
                var recoveryRoot = GetRecoveryRoot(series.Id);
                if (!_diskProvider.FolderExists(recoveryRoot))
                {
                    return;
                }

                foreach (var folder in _diskProvider.GetDirectories(recoveryRoot))
                {
                    ValidateRecoveryPath(recoveryRoot, folder);
                    var renameJournalPath = Path.Combine(folder, "rename.json");
                    if (_diskProvider.FileExists(renameJournalPath))
                    {
                        ValidateRecoveryPath(folder, renameJournalPath);
                        RecoverRename(series, Json.Deserialize<RenameJournal>(_diskProvider.ReadAllText(renameJournalPath)));
                        CleanupReceiptDirectory(folder);
                        continue;
                    }

                    var journalPath = Path.Combine(folder, "import.json");
                    if (!_diskProvider.FileExists(journalPath))
                    {
                        continue;
                    }

                    ValidateRecoveryPath(folder, journalPath);

                    var journal = Json.Deserialize<ImportJournal>(_diskProvider.ReadAllText(journalPath));
                    if (journal == null || journal.SeriesId != series.Id || journal.OldFileIds == null || journal.OldLinks == null || journal.OldFiles == null)
                    {
                        throw new IOException("Invalid episode import recovery receipt: " + journalPath);
                    }

                    var workFolder = Path.Combine(series.Path, StagingFolder, Path.GetFileName(folder));
                    if (!Path.GetFullPath(journal.WorkDirectory).PathEquals(Path.GetFullPath(workFolder)))
                    {
                        throw new IOException("Import staging directory does not match its protected receipt.");
                    }

                    ValidateRecoveryPath(Path.Combine(series.Path, StagingFolder), workFolder);
                    MediaFileRecoveryPaths.VerifyResolvedPath(Path.Combine(workFolder, ".receipt"), Path.Combine(journal.ResolvedWorkDirectory, ".receipt"));
                    journal.DestinationPath = ValidateRecoveryPath(series.Path, journal.DestinationPath, true);
                    MediaFileRecoveryPaths.VerifyResolvedPath(journal.DestinationPath, journal.ResolvedDestinationPath);
                    journal.StagedPath = ValidateRecoveryPath(workFolder, journal.StagedPath);
                    MediaFileRecoveryPaths.VerifyResolvedPath(journal.StagedPath, journal.ResolvedStagedPath);
                    if (journal.BackupPath != null)
                    {
                        journal.BackupPath = ValidateRecoveryPath(workFolder, journal.BackupPath);
                        MediaFileRecoveryPaths.VerifyResolvedPath(journal.BackupPath, journal.ResolvedBackupPath);
                    }

                    if (journal.BackupOriginalPath != null)
                    {
                        journal.BackupOriginalPath = ValidateRecoveryPath(series.Path, journal.BackupOriginalPath, true);
                        MediaFileRecoveryPaths.VerifyResolvedPath(journal.BackupOriginalPath, journal.ResolvedBackupOriginalPath);
                    }

                    foreach (var oldFile in journal.OldFiles)
                    {
                        if (oldFile.SeriesId != series.Id || !journal.OldFileIds.Contains(oldFile.Id))
                        {
                            throw new IOException("Import receipt contains a file from another series.");
                        }

                        var originalPath = ValidateRecoveryPath(series.Path, Path.Combine(series.Path, oldFile.RelativePath), true);
                        MediaFileRecoveryPaths.VerifyResolvedPath(originalPath, oldFile.Path);
                    }

                    var candidates = _mediaFileService.GetFilesWithRelativePath(series.Id, series.Path.GetRelativePath(journal.DestinationPath));
                    var committedFile = candidates.SingleOrDefault(f => !journal.OldFileIds.Contains(f.Id) && Math.Abs((f.DateAdded - journal.DateAdded).Ticks) < TimeSpan.TicksPerMillisecond);
                    if (journal.CommittedFileId > 0 || committedFile != null)
                    {
                        CompleteImport(series, journal, new List<DeletedEpisodeFile>(), false);
                    }
                    else
                    {
                        RollbackImport(journal, false);
                    }

                    CleanupImportWork(journal);
                    CleanupReceiptDirectory(folder);
                }
            }
        }

        private void RecoverRename(Series series, RenameJournal journal)
        {
            if (journal == null || journal.SeriesId != series.Id || journal.EpisodeFileId <= 0 ||
                string.IsNullOrWhiteSpace(journal.SourceRelativePath) || string.IsNullOrWhiteSpace(journal.DestinationRelativePath))
            {
                throw new IOException("Invalid episode rename recovery receipt.");
            }

            var sourcePath = ValidateRecoveryPath(series.Path, Path.Combine(series.Path, journal.SourceRelativePath), true);
            MediaFileRecoveryPaths.VerifyResolvedPath(sourcePath, journal.ResolvedSourcePath);
            var destinationPath = ValidateRecoveryPath(series.Path, Path.Combine(series.Path, journal.DestinationRelativePath), true);
            MediaFileRecoveryPaths.VerifyResolvedPath(destinationPath, journal.ResolvedDestinationPath);
            var file = _mediaFileService.Get(new[] { journal.EpisodeFileId }).SingleOrDefault();
            if (file == null || file.SeriesId != series.Id)
            {
                throw new IOException("Renamed episode file is no longer in this series. Its recovery receipt and bytes are preserved.");
            }

            var recordedPath = ValidateRecoveryPath(series.Path, Path.Combine(series.Path, file.RelativePath), true);
            if (_mediaFileService.GetFilesBySeries(series.Id).Any(other => other.Id != file.Id &&
                (Path.Combine(series.Path, other.RelativePath).PathEquals(sourcePath, StringComparison.OrdinalIgnoreCase) ||
                 Path.Combine(series.Path, other.RelativePath).PathEquals(destinationPath, StringComparison.OrdinalIgnoreCase))))
            {
                throw new IOException("Another episode file uses a pending rename path. Both files and the receipt are preserved.");
            }

            if (recordedPath.PathEquals(destinationPath, StringComparison.Ordinal))
            {
                if (!_diskProvider.FileExists(journal.ResolvedDestinationPath))
                {
                    throw new IOException("Committed rename destination is missing. Its recovery receipt is preserved.");
                }

                _extraService.MoveFilesAfterRename(series, file, true);

                _eventAggregator.PublishEvent(new EpisodeFileRenamedEvent(series, file, sourcePath));
                _eventAggregator.PublishEvent(new SeriesRenamedEvent(series, new List<RenamedEpisodeFile>
                {
                    new RenamedEpisodeFile { EpisodeFile = file, PreviousPath = sourcePath, PreviousRelativePath = journal.SourceRelativePath }
                }));

                return;
            }

            if (!recordedPath.PathEquals(sourcePath, StringComparison.Ordinal))
            {
                throw new IOException("Episode file path changed after a pending rename. Its recovery receipt is preserved.");
            }

            var sourceExists = _diskProvider.FileExists(journal.ResolvedSourcePath);
            var destinationExists = _diskProvider.FileExists(journal.ResolvedDestinationPath);
            if (!sourceExists && !destinationExists && sourcePath.PathEquals(destinationPath, StringComparison.OrdinalIgnoreCase))
            {
                var intermediatePath = ValidateRecoveryPath(series.Path, sourcePath + ".backup~", true);
                if (_diskProvider.FileExists(intermediatePath))
                {
                    _diskProvider.MoveFile(MediaFileRecoveryPaths.ResolveFilePath(intermediatePath), journal.ResolvedSourcePath);
                    return;
                }
            }

            if (sourcePath.PathEquals(destinationPath) && destinationExists)
            {
                // Case-only rename on a case-insensitive filesystem still addresses one physical file.
                _diskProvider.MoveFile(journal.ResolvedDestinationPath, journal.ResolvedSourcePath);
                return;
            }

            if (sourceExists && !destinationExists)
            {
                return;
            }

            if (!sourceExists && destinationExists)
            {
                _diskProvider.MoveFile(journal.ResolvedDestinationPath, journal.ResolvedSourcePath);
                return;
            }

            throw new IOException("Pending rename has conflicting or missing files. Its bytes and recovery receipt are preserved.");
        }

        private string GetRecoveryRoot(int seriesId)
        {
            var root = MediaFileRecoveryPaths.GetJournalRoot(_appFolderInfo, _diskProvider);
            return ValidateRecoveryPath(root, Path.Combine(root, "files", seriesId.ToString()));
        }

        private static bool HasSameStem(string first, string second)
        {
            return Path.GetDirectoryName(first).PathEquals(Path.GetDirectoryName(second), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFileNameWithoutExtension(first), Path.GetFileNameWithoutExtension(second), StringComparison.OrdinalIgnoreCase);
        }

        private string ValidateRecoveryPath(string root, string path, bool allowSymbolicLinks = false)
        {
            return MediaFileRecoveryPaths.ValidateContainedPath(_diskProvider, root, path, allowSymbolicLinks);
        }

        private static EpisodeFile SnapshotFile(EpisodeFile file, string seriesPath)
        {
            return new EpisodeFile
            {
                Id = file.Id,
                Path = MediaFileRecoveryPaths.ResolveFilePath(Path.Combine(seriesPath, file.RelativePath)),
                SeriesId = file.SeriesId,
                SeasonNumber = file.SeasonNumber,
                RelativePath = file.RelativePath,
                Size = file.Size,
                DateAdded = file.DateAdded,
                OriginalFilePath = file.OriginalFilePath,
                SceneName = file.SceneName,
                ReleaseGroup = file.ReleaseGroup,
                ReleaseHash = file.ReleaseHash,
                Quality = file.Quality,
                IndexerFlags = file.IndexerFlags,
                MediaInfo = file.MediaInfo,
                Languages = file.Languages,
                ReleaseType = file.ReleaseType
            };
        }

        private void WriteJournal<T>(string path, T journal)
        {
            MediaFileRecoveryPaths.WriteJournal(_diskProvider, path, journal);
        }

        private void RollbackImport(ImportJournal journal, bool restoreSource = true)
        {
            MediaFileRecoveryPaths.VerifyResolvedPath(journal.DestinationPath, journal.ResolvedDestinationPath);
            if (journal.BackupOriginalPath != null)
            {
                MediaFileRecoveryPaths.VerifyResolvedPath(journal.BackupOriginalPath, journal.ResolvedBackupOriginalPath);
            }

            var backedUp = journal.BackupReady && journal.BackupPath != null && _diskProvider.FileExists(journal.ResolvedBackupPath);
            if (backedUp && _diskProvider.GetFileSize(journal.ResolvedBackupPath) != journal.BackupSize)
            {
                throw new IOException("Episode recovery backup changed size. Its bytes and receipt are preserved.");
            }

            if (_diskProvider.FileExists(journal.ResolvedDestinationPath) && (backedUp || !journal.DestinationExisted))
            {
                if (restoreSource && !_diskProvider.FileExists(journal.ResolvedSourcePath))
                {
                    MediaFileRecoveryPaths.VerifyResolvedPath(journal.ResolvedSourcePath, journal.ResolvedSourcePath);
                    _diskProvider.MoveFile(journal.ResolvedDestinationPath, journal.ResolvedSourcePath);
                }
                else
                {
                    _diskProvider.DeleteFile(journal.ResolvedDestinationPath);
                }
            }

            if (backedUp)
            {
                _diskProvider.MoveFile(journal.ResolvedBackupPath, journal.ResolvedBackupOriginalPath ?? journal.ResolvedDestinationPath);
            }
        }

        private void CleanupReceiptDirectory(string directory)
        {
            foreach (var name in new[] { "import.json", "import.json.new", "rename.json", "rename.json.new" })
            {
                var path = ValidateRecoveryPath(directory, Path.Combine(directory, name));
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory, false);
            }
        }

        private void CleanupImportWork(ImportJournal journal)
        {
            var work = journal.ResolvedWorkDirectory;
            MediaFileRecoveryPaths.VerifyResolvedPath(Path.Combine(work, ".receipt"), Path.Combine(work, ".receipt"));
            if (journal.ResolvedBackupPath != null && _diskProvider.FileExists(journal.ResolvedBackupPath))
            {
                ValidateRecoveryPath(work, journal.ResolvedBackupPath);
                if (journal.BackupReady || journal.ResolvedBackupOriginalPath == null || !_diskProvider.FileExists(journal.ResolvedBackupOriginalPath))
                {
                    throw new IOException("An unrecovered episode backup remains. Its bytes and receipt are preserved.");
                }

                _diskProvider.DeleteFile(journal.ResolvedBackupPath);
            }

            if (_diskProvider.FileExists(journal.ResolvedStagedPath))
            {
                ValidateRecoveryPath(work, journal.ResolvedStagedPath);
                _diskProvider.DeleteFile(journal.ResolvedStagedPath);
            }

            foreach (var folder in new[] { Path.Combine(work, "incoming"), Path.Combine(work, "previous"), work })
            {
                if (_diskProvider.FolderExists(folder) && _diskProvider.FolderEmpty(folder))
                {
                    Directory.Delete(folder, false);
                }
            }
        }

        private void CompleteImport(Series series, ImportJournal journal, List<DeletedEpisodeFile> oldFiles, bool removeSource = true)
        {
            var currentFiles = _mediaFileService.Get(journal.OldFileIds);
            var previousFiles = journal.OldFiles.Count > 0 ? journal.OldFiles : currentFiles;
            foreach (var file in previousFiles)
            {
                var current = currentFiles.SingleOrDefault(f => f.Id == file.Id);
                if (file.SeriesId != series.Id || (current != null && (current.SeriesId != series.Id || current.RelativePath != file.RelativePath)))
                {
                    throw new IOException("Replaced file metadata changed; preserving its import recovery receipt.");
                }

                file.Series = series;
                var knownPath = ValidateRecoveryPath(series.Path, Path.Combine(series.Path, file.RelativePath), true);
                MediaFileRecoveryPaths.VerifyResolvedPath(knownPath, file.Path);
                if (_trackFileService.IsFileReferenced(file.Id))
                {
                    if (journal.BackupPath != null && journal.BackupOriginalPath != null &&
                        Path.Combine(series.Path, file.RelativePath).PathEquals(journal.BackupOriginalPath) && _diskProvider.FileExists(journal.ResolvedBackupPath))
                    {
                        throw new IOException("Replaced file acquired new references. Its backup is preserved at " + journal.BackupPath);
                    }

                    continue;
                }

                var originalPath = Path.Combine(series.Path, file.RelativePath);
                var path = journal.BackupOriginalPath != null && originalPath.PathEquals(journal.BackupOriginalPath, StringComparison.OrdinalIgnoreCase) ? journal.ResolvedBackupPath : file.Path;
                string recycleBinPath = null;
                if (path != null && _diskProvider.FileExists(path))
                {
                    var rootFolder = _diskProvider.GetParentFolder(series.Path);
                    var subfolder = rootFolder.GetRelativePath(_diskProvider.GetParentFolder(originalPath));
                    recycleBinPath = _recycleBinProvider.DeleteFile(path, subfolder);
                }

                oldFiles.Add(new DeletedEpisodeFile(file, recycleBinPath));
                var oldLinks = journal.OldLinks.Where(l => l.EpisodeFileId == file.Id).ToList();
                file.TrackFiles = oldLinks;
                file.Episodes = _episodeService.GetEpisodes(oldLinks.Select(l => l.EpisodeId).Distinct().ToList());
                _mediaFileService.Delete(file, DeleteMediaFileReason.Upgrade);
            }

            if (removeSource && !journal.CopyOnly && !journal.ResolvedSourcePath.PathEquals(journal.ResolvedDestinationPath) && _diskProvider.FileExists(journal.ResolvedSourcePath))
            {
                MediaFileRecoveryPaths.VerifyResolvedPath(journal.SourcePath, journal.ResolvedSourcePath);
                _diskProvider.DeleteFile(journal.ResolvedSourcePath);
            }
        }

        public class ImportJournal
        {
            public int SeriesId { get; set; }
            public string SourcePath { get; set; }
            public string ResolvedSourcePath { get; set; }
            public string WorkDirectory { get; set; }
            public string ResolvedWorkDirectory { get; set; }
            public string DestinationPath { get; set; }
            public string ResolvedDestinationPath { get; set; }
            public string StagedPath { get; set; }
            public string ResolvedStagedPath { get; set; }
            public string BackupPath { get; set; }
            public string ResolvedBackupPath { get; set; }
            public string BackupOriginalPath { get; set; }
            public string ResolvedBackupOriginalPath { get; set; }
            public bool DestinationExisted { get; set; }
            public bool BackupReady { get; set; }
            public long BackupSize { get; set; }
            public int CommittedFileId { get; set; }
            public List<int> OldFileIds { get; set; }
            public List<EpisodeFile> OldFiles { get; set; } = new();
            public List<EpisodeTrackFile> OldLinks { get; set; } = new();
            public List<int> EpisodeIds { get; set; }
            public List<int> TrackIds { get; set; }
            public bool CopyOnly { get; set; }
            public DateTime DateAdded { get; set; }
        }

        public class RenameJournal
        {
            public int SeriesId { get; set; }
            public int EpisodeFileId { get; set; }
            public string SourceRelativePath { get; set; }
            public string ResolvedSourcePath { get; set; }
            public string DestinationRelativePath { get; set; }
            public string ResolvedDestinationPath { get; set; }
        }
    }
}
