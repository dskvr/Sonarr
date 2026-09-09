using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Extras.Files
{
    public interface IManageExtraFiles
    {
        int Order { get; }
        IEnumerable<ExtraFile> CreateAfterMediaCoverUpdate(Series series);
        IEnumerable<ExtraFile> CreateAfterSeriesScan(Series series, List<EpisodeFile> episodeFiles);
        IEnumerable<ExtraFile> CreateAfterEpisodesImported(Series series);
        IEnumerable<ExtraFile> CreateAfterEpisodeImport(Series series, EpisodeFile episodeFile);
        IEnumerable<ExtraFile> CreateAfterEpisodeFolder(Series series, string seriesFolder, string seasonFolder);
        IEnumerable<ExtraFile> MoveFilesAfterRename(Series series, List<EpisodeFile> episodeFiles, bool requireSuccess = false);
        bool CanImportFile(LocalEpisode localEpisode, EpisodeFile episodeFile, string path, string extension, bool readOnly);
        IEnumerable<ExtraFile> ImportFiles(LocalEpisode localEpisode, EpisodeFile episodeFile, List<string> files, bool isReadOnly);
    }

    public abstract class ExtraFileManager<TExtraFile> : IManageExtraFiles
        where TExtraFile : ExtraFile, new()
    {
        private readonly IConfigService _configService;
        private readonly IDiskProvider _diskProvider;
        private readonly IDiskTransferService _diskTransferService;
        private readonly Logger _logger;
        private readonly IExtraFileService<TExtraFile> _extraFileService;
        private readonly IAppFolderInfo _appFolderInfo;

        public ExtraFileManager(IConfigService configService,
                                IDiskProvider diskProvider,
                                IDiskTransferService diskTransferService,
                                IExtraFileService<TExtraFile> extraFileService,
                                IAppFolderInfo appFolderInfo,
                                Logger logger)
        {
            _configService = configService;
            _diskProvider = diskProvider;
            _diskTransferService = diskTransferService;
            _logger = logger;
            _extraFileService = extraFileService;
            _appFolderInfo = appFolderInfo;
        }

        public abstract int Order { get; }
        public abstract IEnumerable<ExtraFile> CreateAfterMediaCoverUpdate(Series series);
        public abstract IEnumerable<ExtraFile> CreateAfterSeriesScan(Series series, List<EpisodeFile> episodeFiles);
        public abstract IEnumerable<ExtraFile> CreateAfterEpisodesImported(Series series);
        public abstract IEnumerable<ExtraFile> CreateAfterEpisodeImport(Series series, EpisodeFile episodeFile);
        public abstract IEnumerable<ExtraFile> CreateAfterEpisodeFolder(Series series, string seriesFolder, string seasonFolder);
        public abstract IEnumerable<ExtraFile> MoveFilesAfterRename(Series series, List<EpisodeFile> episodeFiles, bool requireSuccess = false);
        public abstract bool CanImportFile(LocalEpisode localEpisode, EpisodeFile episodeFile, string path, string extension, bool readOnly);
        public abstract IEnumerable<ExtraFile> ImportFiles(LocalEpisode localEpisode, EpisodeFile episodeFile, List<string> files, bool isReadOnly);

        protected TExtraFile ImportFile(Series series, EpisodeFile episodeFile, string path, bool readOnly, string extension, string fileNameSuffix = null)
        {
            var newFolder = Path.GetDirectoryName(Path.Combine(series.Path, episodeFile.RelativePath));
            var filenameBuilder = new StringBuilder(Path.GetFileNameWithoutExtension(episodeFile.RelativePath));

            if (fileNameSuffix.IsNotNullOrWhiteSpace())
            {
                filenameBuilder.Append(fileNameSuffix);
            }

            filenameBuilder.Append(extension);

            var newFileName = Path.Combine(newFolder, filenameBuilder.ToString());
            var transferMode = TransferMode.Move;

            if (readOnly)
            {
                transferMode = _configService.CopyUsingHardlinks ? TransferMode.HardLinkOrCopy : TransferMode.Copy;
            }

            EnsureDestinationAvailable(series, episodeFile, newFileName);
            if (path.PathNotEquals(newFileName))
            {
                _diskTransferService.TransferFile(path, newFileName, transferMode, _diskProvider.FileExists(newFileName));
            }

            return new TExtraFile
            {
                SeriesId = series.Id,
                SeasonNumber = episodeFile.SeasonNumber,
                EpisodeFileId = episodeFile.Id,
                RelativePath = series.Path.GetRelativePath(newFileName),
                Extension = extension
            };
        }

        protected TExtraFile MoveFile(Series series, EpisodeFile episodeFile, TExtraFile extraFile, string fileNameSuffix = null, List<Exception> failures = null)
        {
            _logger.Trace("Renaming extra file: {0}", extraFile);

            var newFolder = Path.GetDirectoryName(Path.Combine(series.Path, episodeFile.RelativePath));
            var filenameBuilder = new StringBuilder(Path.GetFileNameWithoutExtension(episodeFile.RelativePath));

            if (fileNameSuffix.IsNotNullOrWhiteSpace())
            {
                filenameBuilder.Append(fileNameSuffix);
            }

            filenameBuilder.Append(extraFile.Extension);

            var newFileName = Path.Combine(newFolder, filenameBuilder.ToString());

            return MoveFileTo(series, episodeFile, extraFile, newFileName, failures);
        }

        protected TExtraFile MoveFileTo(Series series, EpisodeFile episodeFile, TExtraFile extraFile, string newFileName, List<Exception> failures = null)
        {
            var existingFileName = Path.Combine(series.Path, extraFile.RelativePath);
            try
            {
                if (failures != null)
                {
                    return MoveFileWithRecovery(series, episodeFile, extraFile, existingFileName, newFileName);
                }

                if (newFileName.PathEquals(existingFileName))
                {
                    return null;
                }

                EnsureDestinationAvailable(series, episodeFile, newFileName);
                _diskProvider.MoveFile(existingFileName, newFileName);
                extraFile.RelativePath = series.Path.GetRelativePath(newFileName);
                return extraFile;
            }
            catch (Exception ex)
            {
                if (failures != null)
                {
                    failures.Add(ex);
                }
                else
                {
                    _logger.Warn(ex, "Unable to move file after rename: {0}", existingFileName);
                }

                return null;
            }
        }

        protected void EnsureDestinationAvailable(Series series, EpisodeFile episodeFile, string destination)
        {
            var relativePath = series.Path.GetRelativePath(destination);
            var existing = _extraFileService.FindByPath(series.Id, relativePath);
            if (existing != null && existing.EpisodeFileId != episodeFile.Id)
            {
                throw new IOException("Extra file destination belongs to another episode file: " + destination);
            }

            if ((_diskProvider.FileExists(destination) || new FileInfo(destination).LinkTarget != null) && existing == null)
            {
                throw new IOException("Extra file destination already exists without matching ownership: " + destination);
            }
        }

        private TExtraFile MoveFileWithRecovery(Series series, EpisodeFile episodeFile, TExtraFile extraFile, string source, string destination)
        {
            var recoveryRoot = MediaFileRecoveryPaths.GetJournalRoot(_appFolderInfo, _diskProvider);
            var directory = MediaFileRecoveryPaths.ValidateContainedPath(_diskProvider, recoveryRoot, Path.Combine(recoveryRoot, "extras"));
            var receiptPath = MediaFileRecoveryPaths.ValidateContainedPath(_diskProvider, directory, Path.Combine(directory, $"{typeof(TExtraFile).Name}-{series.Id}-{extraFile.Id}.json"));
            SidecarRenameJournal journal;

            if (_diskProvider.FileExists(receiptPath))
            {
                journal = Json.Deserialize<SidecarRenameJournal>(_diskProvider.ReadAllText(receiptPath));
                if (journal == null || journal.SeriesId != series.Id || journal.EpisodeFileId != episodeFile.Id || journal.ExtraFileId != extraFile.Id ||
                    journal.ExtraFileType != typeof(TExtraFile).Name || !destination.PathEquals(journal.DestinationPath) ||
                    (!source.PathEquals(journal.SourcePath) && !source.PathEquals(journal.DestinationPath)))
                {
                    throw new IOException("Sidecar rename receipt does not match its current owner or destination.");
                }
            }
            else
            {
                if (source.PathEquals(destination))
                {
                    return null;
                }

                EnsureDestinationAvailable(series, episodeFile, destination);
                var resolvedSource = MediaFileRecoveryPaths.ResolveFilePath(source);
                var resolvedDestination = MediaFileRecoveryPaths.ResolveFilePath(destination);
                var linkTarget = new FileInfo(resolvedSource).LinkTarget;
                var resolvedLinkTarget = linkTarget == null ? null : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(resolvedSource), linkTarget));
                var temporaryLinkPath = linkTarget == null ? null : destination + ".sonarr-" + Guid.NewGuid().ToString("N") + ".tmp";
                journal = new SidecarRenameJournal
                {
                    SeriesId = series.Id,
                    EpisodeFileId = episodeFile.Id,
                    ExtraFileId = extraFile.Id,
                    ExtraFileType = typeof(TExtraFile).Name,
                    SourcePath = MediaFileRecoveryPaths.ValidateContainedPath(_diskProvider, series.Path, source, true),
                    DestinationPath = MediaFileRecoveryPaths.ValidateContainedPath(_diskProvider, series.Path, destination, true),
                    ResolvedSourcePath = resolvedSource,
                    ResolvedDestinationPath = resolvedDestination,
                    Hash = linkTarget == null ? FileHash(resolvedSource) : null,
                    OriginalLinkTarget = linkTarget,
                    DestinationLinkTarget = linkTarget == null || Path.IsPathRooted(linkTarget) ? linkTarget : Path.GetRelativePath(Path.GetDirectoryName(resolvedDestination), resolvedLinkTarget),
                    ResolvedLinkTarget = resolvedLinkTarget,
                    TemporaryLinkPath = temporaryLinkPath,
                    ResolvedTemporaryLinkPath = temporaryLinkPath == null ? null : MediaFileRecoveryPaths.ResolveFilePath(temporaryLinkPath)
                };
                _diskProvider.CreateFolder(directory);
                MediaFileRecoveryPaths.WriteJournal(_diskProvider, receiptPath, journal);
            }

            MediaFileRecoveryPaths.ValidateContainedPath(_diskProvider, series.Path, journal.SourcePath, true);
            MediaFileRecoveryPaths.ValidateContainedPath(_diskProvider, series.Path, journal.DestinationPath, true);
            VerifyResolvedPaths(journal);
            var owner = _extraFileService.FindByPath(series.Id, series.Path.GetRelativePath(journal.DestinationPath));
            if (owner != null && (owner.Id != extraFile.Id || owner.EpisodeFileId != episodeFile.Id))
            {
                throw new IOException("Sidecar rename destination belongs to another owner.");
            }

            if (journal.OriginalLinkTarget != null)
            {
                return CompleteSymbolicLinkRename(series, extraFile, journal, receiptPath);
            }

            if (!_diskProvider.FileExists(journal.ResolvedDestinationPath))
            {
                VerifyHash(journal.ResolvedSourcePath, journal.Hash);
                VerifyResolvedPaths(journal);
                _diskProvider.MoveFile(journal.ResolvedSourcePath, journal.ResolvedDestinationPath);
            }

            VerifyHash(journal.ResolvedDestinationPath, journal.Hash);
            if (!journal.ResolvedSourcePath.PathEquals(journal.ResolvedDestinationPath) && _diskProvider.FileExists(journal.ResolvedSourcePath))
            {
                VerifyHash(journal.ResolvedSourcePath, journal.Hash);
            }

            VerifyResolvedPaths(journal);
            extraFile.RelativePath = series.Path.GetRelativePath(journal.DestinationPath);
            _extraFileService.Upsert(extraFile);

            VerifyResolvedPaths(journal);
            if (!journal.ResolvedSourcePath.PathEquals(journal.ResolvedDestinationPath) && _diskProvider.FileExists(journal.ResolvedSourcePath))
            {
                VerifyHash(journal.ResolvedSourcePath, journal.Hash);
                VerifyResolvedPaths(journal);
                _diskProvider.DeleteFile(journal.ResolvedSourcePath);
            }

            VerifyResolvedPaths(journal);
            _diskProvider.DeleteFile(receiptPath);
            return extraFile;
        }

        private TExtraFile CompleteSymbolicLinkRename(Series series, TExtraFile extraFile, SidecarRenameJournal journal, string receiptPath)
        {
            MediaFileRecoveryPaths.ValidateContainedPath(_diskProvider, series.Path, journal.TemporaryLinkPath, true);
            VerifyResolvedPaths(journal);
            if (string.IsNullOrWhiteSpace(journal.DestinationLinkTarget) ||
                !Path.GetFullPath(Path.Combine(Path.GetDirectoryName(journal.ResolvedSourcePath), journal.OriginalLinkTarget)).PathEquals(journal.ResolvedLinkTarget) ||
                !Path.GetFullPath(Path.Combine(Path.GetDirectoryName(journal.ResolvedDestinationPath), journal.DestinationLinkTarget)).PathEquals(journal.ResolvedLinkTarget))
            {
                throw new IOException("Sidecar link receipt does not preserve its original target.");
            }

            VerifyLinkIfPresent(journal.ResolvedSourcePath, journal.OriginalLinkTarget);
            var destinationExists = VerifyLinkIfPresent(journal.ResolvedDestinationPath, journal.DestinationLinkTarget);
            if (!destinationExists)
            {
                if (!VerifyLinkIfPresent(journal.ResolvedTemporaryLinkPath, journal.DestinationLinkTarget))
                {
                    // Create the replacement entry before removing the original. Never open its target.
                    VerifyResolvedPaths(journal);
                    File.CreateSymbolicLink(journal.ResolvedTemporaryLinkPath, journal.DestinationLinkTarget);
                }

                // DiskProvider.MoveFile clears read-only flags, which can affect a link's external target.
                VerifyResolvedPaths(journal);
                File.Move(journal.ResolvedTemporaryLinkPath, journal.ResolvedDestinationPath);
            }

            if (!VerifyLinkIfPresent(journal.ResolvedDestinationPath, journal.DestinationLinkTarget))
            {
                throw new IOException("Replacement sidecar link was not created.");
            }

            VerifyResolvedPaths(journal);
            extraFile.RelativePath = series.Path.GetRelativePath(journal.DestinationPath);
            _extraFileService.Upsert(extraFile);

            VerifyResolvedPaths(journal);
            if (!journal.ResolvedSourcePath.PathEquals(journal.ResolvedDestinationPath) && VerifyLinkIfPresent(journal.ResolvedSourcePath, journal.OriginalLinkTarget))
            {
                VerifyResolvedPaths(journal);
                File.Delete(journal.ResolvedSourcePath);
            }

            if (VerifyLinkIfPresent(journal.ResolvedTemporaryLinkPath, journal.DestinationLinkTarget))
            {
                VerifyResolvedPaths(journal);
                File.Delete(journal.ResolvedTemporaryLinkPath);
            }

            VerifyResolvedPaths(journal);
            _diskProvider.DeleteFile(receiptPath);
            return extraFile;
        }

        private static void VerifyResolvedPaths(SidecarRenameJournal journal)
        {
            MediaFileRecoveryPaths.VerifyResolvedPath(journal.SourcePath, journal.ResolvedSourcePath);
            MediaFileRecoveryPaths.VerifyResolvedPath(journal.DestinationPath, journal.ResolvedDestinationPath);
            if (journal.OriginalLinkTarget != null)
            {
                MediaFileRecoveryPaths.VerifyResolvedPath(journal.TemporaryLinkPath, journal.ResolvedTemporaryLinkPath);
            }
        }

        private bool VerifyLinkIfPresent(string path, string expectedTarget)
        {
            var target = new FileInfo(path).LinkTarget;
            if (target == null && !_diskProvider.FileExists(path))
            {
                return false;
            }

            if (!string.Equals(target, expectedTarget, StringComparison.Ordinal))
            {
                throw new IOException("Sidecar link changed; preserving entries and recovery receipt: " + path);
            }

            return true;
        }

        private void VerifyHash(string path, string expectedHash)
        {
            if (!string.Equals(FileHash(path), expectedHash, StringComparison.Ordinal))
            {
                throw new IOException("Sidecar rename bytes changed; preserving file and recovery receipt: " + path);
            }
        }

        private string FileHash(string path)
        {
            if ((_diskProvider.GetFileAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Sidecar recovery cannot operate on a symbolic link: " + path);
            }

            using var stream = _diskProvider.OpenReadStream(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        private sealed class SidecarRenameJournal
        {
            public SidecarRenameJournal()
            {
            }

            public int SeriesId { get; set; }
            public int EpisodeFileId { get; set; }
            public int ExtraFileId { get; set; }
            public string ExtraFileType { get; set; }
            public string SourcePath { get; set; }
            public string DestinationPath { get; set; }
            public string ResolvedSourcePath { get; set; }
            public string ResolvedDestinationPath { get; set; }
            public string Hash { get; set; }
            public string OriginalLinkTarget { get; set; }
            public string DestinationLinkTarget { get; set; }
            public string ResolvedLinkTarget { get; set; }
            public string TemporaryLinkPath { get; set; }
            public string ResolvedTemporaryLinkPath { get; set; }
        }
    }
}
