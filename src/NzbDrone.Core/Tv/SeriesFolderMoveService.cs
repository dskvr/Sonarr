using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Tv.Events;

namespace NzbDrone.Core.Tv
{
    public class SeriesFolderMoveService : ISeriesFolderMoveService
    {
        private readonly ISeriesRepository _seriesRepository;
        private readonly IMediaFileService _mediaFileService;
        private readonly IDiskProvider _diskProvider;
        private readonly IDiskTransferService _diskTransferService;
        private readonly IAppFolderInfo _appFolderInfo;
        private readonly IEventAggregator _eventAggregator;

        public SeriesFolderMoveService(ISeriesRepository seriesRepository,
                                       IMediaFileService mediaFileService,
                                       IDiskProvider diskProvider,
                                       IDiskTransferService diskTransferService,
                                       IAppFolderInfo appFolderInfo,
                                       IEventAggregator eventAggregator)
        {
            _seriesRepository = seriesRepository;
            _mediaFileService = mediaFileService;
            _diskProvider = diskProvider;
            _diskTransferService = diskTransferService;
            _appFolderInfo = appFolderInfo;
            _eventAggregator = eventAggregator;
        }

        public void Move(Series series, string sourcePath, string destinationPath)
        {
            using var operationLock = MediaFileOperationLock.AcquireAll();
            Recover(series);
            sourcePath = Path.GetFullPath(sourcePath);
            destinationPath = Path.GetFullPath(destinationPath);
            if (!sourcePath.PathEquals(series.Path))
            {
                throw new IOException("Series folder changed since this move was requested. Request the move again.");
            }

            if (string.Equals(sourcePath, destinationPath, StringComparison.Ordinal))
            {
                return;
            }

            ValidateOwnership(series.Id, sourcePath, destinationPath);

            if (Path.GetDirectoryName(sourcePath) == null || Path.GetDirectoryName(destinationPath) == null)
            {
                throw new IOException("The source and destination must be separate series folders, not filesystem roots.");
            }

            var resolvedSourcePath = MediaFileRecoveryPaths.ResolveFilePath(sourcePath);
            var resolvedDestinationPath = MediaFileRecoveryPaths.ResolveFilePath(destinationPath);
            var rootTarget = ReadLinkTarget(resolvedSourcePath, true);
            ValidateRoots(sourcePath, destinationPath, rootTarget != null, rootTarget != null && resolvedSourcePath.PathEquals(resolvedDestinationPath, StringComparison.OrdinalIgnoreCase));
            MediaFileRecoveryPaths.VerifyResolvedPath(sourcePath, resolvedSourcePath);
            MediaFileRecoveryPaths.VerifyResolvedPath(destinationPath, resolvedDestinationPath);
            if (!FolderEntryExists(resolvedSourcePath))
            {
                if (_mediaFileService.GetFilesBySeries(series.Id).Any())
                {
                    throw new IOException("The series folder is unavailable. Its path and recorded files were preserved.");
                }

                CommitPath(series, destinationPath);
                return;
            }

            var journal = new MoveJournal
            {
                SeriesId = series.Id,
                SourcePath = sourcePath,
                DestinationPath = destinationPath,
                ResolvedSourcePath = resolvedSourcePath,
                ResolvedDestinationPath = resolvedDestinationPath,
                OperationId = Guid.NewGuid().ToString("N"),
                DestinationExisted = _diskProvider.FolderExists(resolvedDestinationPath),
                AtomicMove = true,
                CaseOnly = sourcePath.PathEquals(destinationPath, StringComparison.OrdinalIgnoreCase)
            };
            if (string.Equals(journal.ResolvedSourcePath, journal.ResolvedDestinationPath, StringComparison.Ordinal))
            {
                CommitPath(series, destinationPath);
                return;
            }

            journal.CaseOnly = journal.ResolvedSourcePath.PathEquals(journal.ResolvedDestinationPath, StringComparison.OrdinalIgnoreCase);
            ValidateRoots(journal.ResolvedSourcePath, journal.ResolvedDestinationPath, rootTarget != null, rootTarget != null && journal.CaseOnly);
            if (rootTarget != null)
            {
                var resolved = ResolveLinkTarget(journal.ResolvedSourcePath, rootTarget, true);
                if (resolved.PathEquals(journal.ResolvedDestinationPath) || resolved.IsParentPath(journal.ResolvedDestinationPath))
                {
                    throw new IOException("The destination would place the series link inside its own target.");
                }

                journal.RootLink = new MoveLink
                {
                    OriginalTarget = rootTarget,
                    DestinationTarget = Path.IsPathRooted(rootTarget) ? resolved : Path.GetRelativePath(Path.GetDirectoryName(journal.ResolvedDestinationPath), resolved),
                    ResolvedTarget = resolved,
                    IsDirectory = true
                };
            }
            else
            {
                journal.Links = ReadTree(journal.ResolvedSourcePath).Links;
                PrepareLinks(journal);
            }

            var journalPath = GetJournalPath(series.Id);
            _diskProvider.CreateFolder(Path.GetDirectoryName(journalPath));
            _diskProvider.CreateFolder(Path.GetDirectoryName(journal.ResolvedDestinationPath));

            if (journal.DestinationExisted && !journal.CaseOnly)
            {
                PrepareCopy(journal);
            }

            MediaFileRecoveryPaths.WriteJournal(_diskProvider, journalPath, journal);
            try
            {
                ValidateLocations(journal);
                if (journal.CaseOnly)
                {
                    MoveAtomicEntry(journal.ResolvedSourcePath, StagingPath(journal), journal.RootLink != null);
                    if (FolderEntryExists(journal.ResolvedDestinationPath))
                    {
                        throw new IOException("A different destination folder already exists with the requested capitalization.");
                    }

                    MoveAtomicEntry(StagingPath(journal, journal.RootLink != null), journal.ResolvedDestinationPath, journal.RootLink != null);
                }
                else if (journal.AtomicMove)
                {
                    try
                    {
                        _diskProvider.MoveFolder(journal.ResolvedSourcePath, journal.ResolvedDestinationPath);
                    }
                    catch (IOException) when (FolderEntryExists(journal.ResolvedSourcePath) && !FolderEntryExists(journal.ResolvedDestinationPath))
                    {
                        PrepareCopy(journal);
                        MediaFileRecoveryPaths.WriteJournal(_diskProvider, journalPath, journal);
                    }
                }

                if (!journal.AtomicMove)
                {
                    CopyAndPublish(journal, journalPath);
                }
                else
                {
                    SetLinkTargets(journal, journal.ResolvedDestinationPath, true, false);
                }

                CommitPath(series, destinationPath);
                Recover(series);
            }
            catch
            {
                Recover(series);
                if (string.Equals(Path.GetFullPath(series.Path), destinationPath, StringComparison.Ordinal))
                {
                    return;
                }

                throw;
            }
        }

        public void Recover(Series series)
        {
            var journalPath = GetJournalPath(series.Id);
            if (!_diskProvider.FileExists(journalPath))
            {
                series.Path = _seriesRepository.Get(series.Id).Path;
                return;
            }

            using var operationLock = MediaFileOperationLock.AcquireAll();
            series.Path = _seriesRepository.Get(series.Id).Path;
            if (!_diskProvider.FileExists(journalPath))
            {
                return;
            }

            var journal = Json.Deserialize<MoveJournal>(_diskProvider.ReadAllText(journalPath));
            ValidateJournal(series, journal);
            ValidateOwnership(series.Id, journal.SourcePath, journal.DestinationPath);
            var committed = string.Equals(Path.GetFullPath(series.Path), journal.DestinationPath, StringComparison.Ordinal);
            if (journal.AtomicMove)
            {
                RecoverAtomic(journal, committed);
                SetLinkTargets(journal, committed ? journal.ResolvedDestinationPath : journal.ResolvedSourcePath, committed);
            }
            else if (journal.RootLink != null)
            {
                RecoverRootLinkCopy(journal, committed);
            }
            else if (committed)
            {
                CompleteCopy(journal);
            }
            else
            {
                RollbackCopy(journal);
            }

            _diskProvider.DeleteFile(journalPath);
        }

        public bool HasPendingMove(int seriesId)
        {
            return _diskProvider.FileExists(GetJournalPath(seriesId));
        }

        public void RecoverPending(IEnumerable<int> seriesIds, IEnumerable<string> paths)
        {
            using var operationLock = MediaFileOperationLock.AcquireAll();
            var ids = seriesIds.Where(id => id > 0).ToHashSet();
            var requestedPaths = paths.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
            var directory = Path.GetDirectoryName(GetJournalPath(1));
            if (!_diskProvider.FolderExists(directory))
            {
                return;
            }

            foreach (var receipt in _diskProvider.GetFiles(directory, false).Where(path => Path.GetExtension(path) == ".json"))
            {
                var safePath = MediaFileRecoveryPaths.ValidateContainedPath(_diskProvider, directory, receipt);
                var journal = Json.Deserialize<MoveJournal>(_diskProvider.ReadAllText(safePath));
                if (journal == null || journal.SourcePath == null || journal.DestinationPath == null ||
                    !safePath.PathEquals(GetJournalPath(journal.SeriesId)))
                {
                    throw new IOException("An invalid pending series move must be reviewed before editing library paths.");
                }

                if (!ids.Contains(journal.SeriesId) && !requestedPaths.Any(path => SeriesRepository.PathsOverlap(path, journal.SourcePath) ||
                    SeriesRepository.PathsOverlap(path, journal.DestinationPath) || SeriesRepository.PathsOverlap(path, journal.ResolvedSourcePath) ||
                    SeriesRepository.PathsOverlap(path, journal.ResolvedDestinationPath)))
                {
                    continue;
                }

                var series = _seriesRepository.Find(journal.SeriesId);
                if (series == null)
                {
                    throw new IOException("A pending move belongs to a removed series. Its files and receipt were preserved.");
                }

                Recover(series);
            }
        }

        private void ValidateOwnership(int seriesId, string source, string destination)
        {
            if (_seriesRepository.HasPathConflict(seriesId, source) || _seriesRepository.HasPathConflict(seriesId, destination))
            {
                throw new IOException("Another series owns the source or destination folder, or an overlapping folder. No files were moved.");
            }
        }

        private void CommitPath(Series series, string path)
        {
            var previous = _seriesRepository.Get(series.Id);
            var current = _seriesRepository.UpdatePath(series.Id, path);
            series.Path = path;
            _eventAggregator.PublishEvent(new SeriesEditedEvent(current, previous, false));
        }

        private void PrepareCopy(MoveJournal journal)
        {
            journal.AtomicMove = false;
            if (journal.RootLink != null)
            {
                if (journal.DestinationExisted)
                {
                    throw new IOException("The destination already contains a folder. A series link cannot replace it.");
                }

                VerifyLink(journal.ResolvedSourcePath, journal.RootLink.OriginalTarget, true);
                return;
            }

            var tree = ReadTree(journal.ResolvedSourcePath);
            journal.Directories = tree.Directories;
            journal.Links = tree.Links;
            PrepareLinks(journal);
            journal.Files = tree.Files.Select(path => new MoveFile
            {
                RelativePath = path
            }).ToList();
            var paths = journal.Files.Select(f => f.RelativePath).Concat(journal.Links.Select(l => l.RelativePath)).ToList();
            if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Count)
            {
                throw new IOException("Source files differ only by capitalization and cannot be moved safely to this destination.");
            }

            PreflightDestination(journal);
            foreach (var file in journal.Files)
            {
                file.Hash = HashFile(Contained(journal.ResolvedSourcePath, file.RelativePath));
            }
        }

        private void PreflightDestination(MoveJournal journal)
        {
            if (!_diskProvider.FolderExists(journal.ResolvedDestinationPath))
            {
                return;
            }

            var target = ReadTree(journal.ResolvedDestinationPath);
            var existingFiles = target.Files.Concat(target.Links.Select(l => l.RelativePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var existingDirectories = target.Directories.ToDictionary(d => d, StringComparer.OrdinalIgnoreCase);
            if (journal.Files.Select(f => f.RelativePath).Concat(journal.Links.Select(l => l.RelativePath)).Any(path => existingFiles.Contains(path) || existingDirectories.ContainsKey(path)) ||
                journal.Directories.Any(d => existingFiles.Contains(d) ||
                    (existingDirectories.TryGetValue(d, out var actual) && !string.Equals(actual, d, StringComparison.Ordinal))))
            {
                throw new IOException("The destination contains conflicting files or folder names. No files were overwritten.");
            }
        }

        private void CopyAndPublish(MoveJournal journal, string journalPath)
        {
            ValidateLocations(journal);
            var stagingPath = StagingPath(journal);
            if (_diskProvider.FolderExists(stagingPath) || _diskProvider.FileExists(stagingPath))
            {
                throw new IOException("The series move staging path is already occupied.");
            }

            if (journal.RootLink != null)
            {
                VerifyLink(journal.ResolvedSourcePath, journal.RootLink.OriginalTarget, true);
                CreateLink(stagingPath, journal.RootLink.DestinationTarget, true);
                journal.Prepared = true;
                MediaFileRecoveryPaths.WriteJournal(_diskProvider, journalPath, journal);
                MoveLinkEntry(stagingPath, journal.ResolvedDestinationPath, true);
                return;
            }

            _diskProvider.CreateFolder(stagingPath);
            foreach (var directory in journal.Directories)
            {
                _diskProvider.CreateFolder(Contained(stagingPath, directory));
            }

            foreach (var file in journal.Files)
            {
                ValidateLocations(journal);
                var target = Contained(stagingPath, file.RelativePath);
                _diskTransferService.TransferFile(Contained(journal.ResolvedSourcePath, file.RelativePath), target, TransferMode.Copy);
                VerifyFile(target, file.Hash);
            }

            foreach (var link in journal.Links)
            {
                CreateLink(Contained(stagingPath, link.RelativePath), link.DestinationTarget, link.IsDirectory);
            }

            VerifySource(journal, false);
            journal.Prepared = true;
            MediaFileRecoveryPaths.WriteJournal(_diskProvider, journalPath, journal);
            PreflightDestination(journal);
            ValidateLocations(journal);
            if (!journal.DestinationExisted)
            {
                _diskProvider.MoveFolder(stagingPath, journal.ResolvedDestinationPath);
                return;
            }

            foreach (var directory in journal.Directories)
            {
                _diskProvider.CreateFolder(Contained(journal.ResolvedDestinationPath, directory));
            }

            foreach (var file in journal.Files)
            {
                _diskProvider.MoveFile(Contained(stagingPath, file.RelativePath), Contained(journal.ResolvedDestinationPath, file.RelativePath));
            }

            foreach (var link in journal.Links)
            {
                var source = LinkPath(stagingPath, link.RelativePath);
                var destination = Contained(journal.ResolvedDestinationPath, link.RelativePath);
                MoveLinkEntry(source, destination, link.IsDirectory);
            }
        }

        private void RecoverAtomic(MoveJournal journal, bool committed)
        {
            var stagingPath = StagingPath(journal, journal.RootLink != null);
            var desired = committed ? journal.ResolvedDestinationPath : journal.ResolvedSourcePath;
            var other = committed ? journal.ResolvedSourcePath : journal.ResolvedDestinationPath;
            if (FolderEntryExists(stagingPath))
            {
                if (FolderEntryExists(desired))
                {
                    throw new IOException("Both the series folder and its temporary move folder exist. Recovery was preserved for review.");
                }

                MoveAtomicEntry(stagingPath, desired, journal.RootLink != null);
                return;
            }

            if (FolderEntryExists(desired))
            {
                if (!committed && !journal.CaseOnly && FolderEntryExists(other))
                {
                    throw new IOException("Both source and destination folders exist. Recovery was preserved for review.");
                }

                if (!committed && journal.CaseOnly && FolderEntryExists(other))
                {
                    MoveAtomicEntry(other, stagingPath, journal.RootLink != null);
                    if (FolderEntryExists(desired))
                    {
                        MoveAtomicEntry(stagingPath, other, journal.RootLink != null);
                        throw new IOException("Both source and destination folders exist. Recovery was preserved for review.");
                    }

                    MoveAtomicEntry(stagingPath, desired, journal.RootLink != null);
                }

                return;
            }

            if (!committed && FolderEntryExists(other))
            {
                MoveAtomicEntry(other, desired, journal.RootLink != null);
                return;
            }

            if (journal.RootLink != null && (ReadLinkTarget(RootLinkArtifact(journal, "backup"), true) != null ||
                ReadLinkTarget(RootLinkArtifact(journal, "new"), true) != null))
            {
                return;
            }

            throw new IOException("The series folder is unavailable during move recovery. Its recovery receipt was preserved.");
        }

        private void RecoverRootLinkCopy(MoveJournal journal, bool committed)
        {
            var link = journal.RootLink;
            var staging = StagingPath(journal, true);
            if (committed)
            {
                VerifyLink(journal.ResolvedDestinationPath, link.DestinationTarget, true);
                if (ReadLinkTarget(journal.ResolvedSourcePath, true) != null)
                {
                    VerifyLink(journal.ResolvedSourcePath, link.OriginalTarget, true);
                    DeleteLink(journal.ResolvedSourcePath, true);
                }
                else if (FolderEntryExists(journal.ResolvedSourcePath) || _diskProvider.FileExists(journal.ResolvedSourcePath))
                {
                    throw new IOException("The original series link was replaced. Its recovery receipt was preserved.");
                }
            }
            else
            {
                VerifyLink(journal.ResolvedSourcePath, link.OriginalTarget, true);
                if (journal.Prepared && ReadLinkTarget(staging, true) == null && ReadLinkTarget(journal.ResolvedDestinationPath, true) != null)
                {
                    VerifyLink(journal.ResolvedDestinationPath, link.DestinationTarget, true);
                    DeleteLink(journal.ResolvedDestinationPath, true);
                }
            }

            if (ReadLinkTarget(staging, true) != null)
            {
                VerifyLink(staging, link.DestinationTarget, true);
                DeleteLink(staging, true);
            }
            else if (_diskProvider.FileExists(staging) || _diskProvider.FolderExists(staging))
            {
                throw new IOException("The series link staging path was replaced. Its recovery receipt was preserved.");
            }
        }

        private void RollbackCopy(MoveJournal journal)
        {
            if (!_diskProvider.FolderExists(journal.ResolvedSourcePath))
            {
                throw new IOException("The original series folder is unavailable. Move recovery was preserved.");
            }

            var stagingPath = StagingPath(journal);
            var publishedWholeFolder = !journal.DestinationExisted && !_diskProvider.FolderExists(stagingPath);
            if (journal.Prepared && (journal.DestinationExisted || publishedWholeFolder))
            {
                foreach (var file in journal.Files)
                {
                    var staged = Contained(stagingPath, file.RelativePath);
                    var destination = Contained(journal.ResolvedDestinationPath, file.RelativePath);
                    if (!_diskProvider.FileExists(staged) && _diskProvider.FileExists(destination))
                    {
                        VerifyFile(destination, file.Hash);
                        _diskProvider.DeleteFile(destination);
                    }
                }

                foreach (var link in journal.Links)
                {
                    var staged = LinkPath(stagingPath, link.RelativePath);
                    var destination = LinkPath(journal.ResolvedDestinationPath, link.RelativePath);
                    if (ReadLinkTarget(staged, link.IsDirectory) == null && ReadLinkTarget(destination, link.IsDirectory) != null)
                    {
                        VerifyLink(destination, link.DestinationTarget, link.IsDirectory);
                        DeleteLink(destination, link.IsDirectory);
                    }
                }

                if (publishedWholeFolder && _diskProvider.FolderExists(journal.ResolvedDestinationPath))
                {
                    RemoveEmptyTree(journal.ResolvedDestinationPath);
                }
            }

            DeleteStaging(journal);
        }

        private void CompleteCopy(MoveJournal journal)
        {
            ValidateLocations(journal);
            foreach (var file in journal.Files)
            {
                VerifyFile(Contained(journal.ResolvedDestinationPath, file.RelativePath), file.Hash);
            }

            foreach (var link in journal.Links)
            {
                VerifyLink(LinkPath(journal.ResolvedDestinationPath, link.RelativePath), link.DestinationTarget, link.IsDirectory);
            }

            if (_diskProvider.FolderExists(journal.ResolvedSourcePath))
            {
                VerifySource(journal, true);
                foreach (var file in journal.Files)
                {
                    var source = Contained(journal.ResolvedSourcePath, file.RelativePath);
                    if (_diskProvider.FileExists(source))
                    {
                        VerifyFile(source, file.Hash);
                        _diskProvider.DeleteFile(source);
                    }
                }

                foreach (var link in journal.Links)
                {
                    var source = LinkPath(journal.ResolvedSourcePath, link.RelativePath);
                    if (ReadLinkTarget(source, link.IsDirectory) != null)
                    {
                        VerifyLink(source, link.OriginalTarget, link.IsDirectory);
                        DeleteLink(source, link.IsDirectory);
                    }
                }

                RemoveEmptyTree(journal.ResolvedSourcePath);
                if (_diskProvider.FolderExists(journal.ResolvedSourcePath))
                {
                    throw new IOException("Additional files appeared during source cleanup. They and the move receipt were preserved.");
                }
            }

            DeleteStaging(journal);
        }

        private void VerifySource(MoveJournal journal, bool allowMissing)
        {
            var tree = ReadTree(journal.ResolvedSourcePath);
            var files = journal.Files.ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
            if ((!allowMissing && tree.Files.Count != files.Count) || tree.Files.Any(path => !files.ContainsKey(path)) ||
                tree.Directories.Except(journal.Directories, StringComparer.Ordinal).Any() ||
                (!allowMissing && tree.Links.Count != journal.Links.Count) ||
                tree.Links.Any(link => !journal.Links.Any(recorded => recorded.RelativePath == link.RelativePath && recorded.OriginalTarget == link.OriginalTarget && recorded.IsDirectory == link.IsDirectory)))
            {
                throw new IOException("Files changed in the original series folder during the move. Both copies were preserved.");
            }

            foreach (var path in tree.Files)
            {
                VerifyFile(Contained(journal.ResolvedSourcePath, path), files[path].Hash);
            }

            foreach (var link in tree.Links)
            {
                var recorded = journal.Links.Single(l => l.RelativePath == link.RelativePath);
                if (!string.Equals(ResolveLinkTarget(LinkPath(journal.ResolvedSourcePath, link.RelativePath), link.OriginalTarget, link.IsDirectory), recorded.ResolvedTarget, StringComparison.Ordinal))
                {
                    throw new IOException("A symbolic link changed its target during the series move. Recovery was preserved.");
                }
            }
        }

        private void DeleteStaging(MoveJournal journal)
        {
            var stagingPath = StagingPath(journal);
            if (_diskProvider.FolderExists(stagingPath))
            {
                var tree = ReadTree(stagingPath);
                if (tree.Files.Except(journal.Files.Select(f => f.RelativePath), StringComparer.Ordinal).Any() ||
                    tree.Directories.Except(journal.Directories, StringComparer.Ordinal).Any() ||
                    tree.Links.Any(link => !journal.Links.Any(recorded => recorded.RelativePath == link.RelativePath && recorded.DestinationTarget == link.OriginalTarget && recorded.IsDirectory == link.IsDirectory)))
                {
                    throw new IOException("Unexpected files appeared in the series move staging folder. Recovery was preserved.");
                }

                foreach (var file in tree.Files)
                {
                    _diskProvider.DeleteFile(Contained(stagingPath, file));
                }

                foreach (var link in tree.Links)
                {
                    DeleteLink(LinkPath(stagingPath, link.RelativePath), link.IsDirectory);
                }

                RemoveEmptyTree(stagingPath);
                if (_diskProvider.FolderExists(stagingPath))
                {
                    throw new IOException("Additional files appeared during staging cleanup. They and the move receipt were preserved.");
                }
            }
        }

        private void RemoveEmptyTree(string root)
        {
            var tree = ReadTree(root);
            foreach (var directory in tree.Directories.OrderByDescending(d => d.Length))
            {
                var path = Contained(root, directory);
                if (!_diskProvider.GetFiles(path, false).Any() && !_diskProvider.GetDirectories(path).Any())
                {
                    Directory.Delete(path, false);
                }
            }

            if (!_diskProvider.GetFiles(root, false).Any() && !_diskProvider.GetDirectories(root).Any())
            {
                Directory.Delete(root, false);
            }
        }

        private (List<string> Directories, List<string> Files, List<MoveLink> Links) ReadTree(string root)
        {
            var directories = new List<string>();
            var files = new List<string>();
            var links = new List<MoveLink>();
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                foreach (var path in _diskProvider.GetDirectories(current))
                {
                    var relative = Path.GetRelativePath(root, path);
                    var target = ReadLinkTarget(path, true);
                    if (target != null)
                    {
                        LinkPath(root, relative);
                        links.Add(new MoveLink { RelativePath = relative, OriginalTarget = target, IsDirectory = true });
                        continue;
                    }

                    pending.Push(Contained(root, relative));
                    directories.Add(relative);
                }

                foreach (var path in _diskProvider.GetFiles(current, false))
                {
                    var relative = Path.GetRelativePath(root, path);
                    var target = ReadLinkTarget(path, false);
                    if (target != null)
                    {
                        LinkPath(root, relative);
                        links.Add(new MoveLink { RelativePath = relative, OriginalTarget = target });
                        continue;
                    }

                    Contained(root, relative);
                    files.Add(relative);
                }
            }

            return (directories, files, links);
        }

        private void ValidateRoots(string source, string destination, bool allowSourceLink = false, bool allowDestinationLink = false)
        {
            if (Path.GetDirectoryName(source) == null || Path.GetDirectoryName(destination) == null ||
                source.IsParentPath(destination) || destination.IsParentPath(source) || _diskProvider.FileExists(destination))
            {
                throw new IOException("The destination must be a separate series folder, not a file or a parent or child of the source.");
            }

            if (!allowDestinationLink && ReadLinkTarget(destination, true) != null)
            {
                throw new IOException("The destination already contains a symbolic link. No files were moved.");
            }

            MediaFileRecoveryPaths.ValidateContainedPath(_diskProvider, Path.GetDirectoryName(source), source, allowSourceLink);
            MediaFileRecoveryPaths.ValidateContainedPath(_diskProvider, Path.GetDirectoryName(destination), destination, allowDestinationLink);
        }

        private void ValidateJournal(Series series, MoveJournal journal)
        {
            if (journal == null || journal.SeriesId != series.Id || !Guid.TryParseExact(journal.OperationId, "N", out _) ||
                journal.SourcePath == null || journal.DestinationPath == null || journal.Files == null || journal.Directories == null || journal.Links == null ||
                !Path.IsPathFullyQualified(journal.SourcePath) || !Path.IsPathFullyQualified(journal.DestinationPath) ||
                (!series.Path.PathEquals(journal.SourcePath) && !series.Path.PathEquals(journal.DestinationPath)))
            {
                throw new IOException("Invalid series move recovery receipt. No files were changed.");
            }

            ValidateRoots(journal.SourcePath, journal.DestinationPath, journal.RootLink != null, journal.RootLink != null);
            ValidateLocations(journal);
            ValidateRoots(journal.ResolvedSourcePath, journal.ResolvedDestinationPath, journal.RootLink != null, journal.RootLink != null);
            StagingPath(journal, journal.RootLink != null);
            foreach (var path in journal.Files.Select(f => f.RelativePath).Concat(journal.Directories))
            {
                Contained(journal.ResolvedSourcePath, path);
                Contained(journal.ResolvedDestinationPath, path);
                Contained(StagingPath(journal), path);
            }

            foreach (var link in journal.Links)
            {
                LinkPath(journal.ResolvedSourcePath, link.RelativePath);
                LinkPath(journal.ResolvedDestinationPath, link.RelativePath);
                LinkPath(StagingPath(journal), link.RelativePath);
                if (string.IsNullOrWhiteSpace(link.OriginalTarget) || string.IsNullOrWhiteSpace(link.DestinationTarget) || string.IsNullOrWhiteSpace(link.ResolvedTarget))
                {
                    throw new IOException("Invalid symbolic link in series move recovery receipt.");
                }
            }

            if (journal.RootLink != null && (!journal.RootLink.IsDirectory || string.IsNullOrWhiteSpace(journal.RootLink.OriginalTarget) ||
                string.IsNullOrWhiteSpace(journal.RootLink.DestinationTarget) || string.IsNullOrWhiteSpace(journal.RootLink.ResolvedTarget)))
            {
                throw new IOException("Invalid series root link recovery receipt.");
            }
        }

        private void PrepareLinks(MoveJournal journal)
        {
            foreach (var link in journal.Links)
            {
                var source = LinkPath(journal.ResolvedSourcePath, link.RelativePath);
                link.ResolvedTarget = ResolveLinkTarget(source, link.OriginalTarget, link.IsDirectory);
                var internalTarget = journal.ResolvedSourcePath.PathEquals(link.ResolvedTarget) || journal.ResolvedSourcePath.IsParentPath(link.ResolvedTarget);
                var destinationTarget = internalTarget
                    ? Path.GetFullPath(Path.Combine(journal.ResolvedDestinationPath, Path.GetRelativePath(journal.ResolvedSourcePath, link.ResolvedTarget)))
                    : link.ResolvedTarget;
                link.DestinationTarget = Path.IsPathRooted(link.OriginalTarget)
                    ? destinationTarget
                    : Path.GetRelativePath(Path.GetDirectoryName(Path.Combine(journal.ResolvedDestinationPath, link.RelativePath)), destinationTarget);
            }
        }

        private void SetLinkTargets(MoveJournal journal, string root, bool committed, bool cleanup = true)
        {
            foreach (var link in journal.RootLink == null ? journal.Links : journal.Links.Append(journal.RootLink))
            {
                var rootLink = ReferenceEquals(link, journal.RootLink);
                var path = rootLink ? root : LinkPath(root, link.RelativePath);
                var temporary = rootLink
                    ? RootLinkArtifact(journal, "new")
                    : LinkPath(root, link.RelativePath + ".sonarr-link-new-" + journal.OperationId);
                var backup = rootLink
                    ? RootLinkArtifact(journal, "backup")
                    : LinkPath(root, link.RelativePath + ".sonarr-link-backup-" + journal.OperationId);
                var target = committed ? link.DestinationTarget : link.OriginalTarget;
                var previous = committed ? link.OriginalTarget : link.DestinationTarget;
                var actual = ReadLinkTarget(path, link.IsDirectory);
                var backedUp = ReadLinkTarget(backup, link.IsDirectory);
                if (actual != target && backedUp == target)
                {
                    if (actual != null)
                    {
                        VerifyLink(path, previous, link.IsDirectory);
                        DeleteLink(path, link.IsDirectory);
                    }
                    else if (_diskProvider.FileExists(path) || _diskProvider.FolderExists(path))
                    {
                        throw new IOException("Another file occupies a symbolic link path. Recovery was preserved.");
                    }

                    MoveLinkEntry(backup, path, link.IsDirectory);
                    actual = target;
                    backedUp = null;
                }

                if (actual != target)
                {
                    if ((actual != null && actual != previous) ||
                        (actual == null && (_diskProvider.FileExists(path) || _diskProvider.FolderExists(path))))
                    {
                        throw new IOException("A symbolic link changed during the series move. Recovery was preserved.");
                    }

                    var prepared = ReadLinkTarget(temporary, link.IsDirectory);
                    if (prepared == null)
                    {
                        CreateLink(temporary, target, link.IsDirectory);
                    }
                    else
                    {
                        VerifyLink(temporary, target, link.IsDirectory);
                    }

                    if (actual != null)
                    {
                        if (backedUp != null || _diskProvider.FileExists(backup) || _diskProvider.FolderExists(backup))
                        {
                            throw new IOException("A symbolic link backup path is occupied. Recovery was preserved.");
                        }

                        MoveLinkEntry(path, backup, link.IsDirectory);
                    }

                    MoveLinkEntry(temporary, path, link.IsDirectory);
                }

                if (cleanup)
                {
                    DeleteLinkArtifact(backup, link);
                    DeleteLinkArtifact(temporary, link);
                }
            }
        }

        private static void DeleteLinkArtifact(string path, MoveLink link)
        {
            var target = ReadLinkTarget(path, link.IsDirectory);
            if (target != null)
            {
                if (target != link.OriginalTarget && target != link.DestinationTarget)
                {
                    throw new IOException("A symbolic link recovery artifact changed. It was preserved.");
                }

                DeleteLink(path, link.IsDirectory);
            }
            else if (File.Exists(path) || Directory.Exists(path))
            {
                throw new IOException("An unexpected file occupies a symbolic link recovery path. It was preserved.");
            }
        }

        private static void MoveLinkEntry(string source, string destination, bool directory)
        {
            if (ReadLinkTarget(source, directory) == null)
            {
                throw new IOException("A symbolic link was replaced before it could be moved. Recovery was preserved.");
            }

            if (directory && (OsInfo.IsWindows || Directory.Exists(source)))
            {
                Directory.Move(source, destination);
            }
            else
            {
                File.Move(source, destination);
            }
        }

        private string LinkPath(string root, string relative)
        {
            var path = MediaFileRecoveryPaths.ValidateContainedPath(_diskProvider, root, Path.Combine(root, relative), true);
            var parent = Path.GetDirectoryName(path);
            if (!parent.PathEquals(root))
            {
                MediaFileRecoveryPaths.ValidateContainedPath(_diskProvider, root, parent);
            }

            return path;
        }

        private string RootLinkArtifact(MoveJournal journal, string kind)
        {
            return MediaFileRecoveryPaths.ValidateContainedPath(_diskProvider, Path.GetDirectoryName(journal.ResolvedDestinationPath), journal.ResolvedDestinationPath + ".sonarr-root-link-" + kind + "-" + journal.OperationId, true);
        }

        private bool FolderEntryExists(string path)
        {
            return _diskProvider.FolderExists(path) || ReadLinkTarget(path, true) != null;
        }

        private void MoveAtomicEntry(string source, string destination, bool link)
        {
            if (link)
            {
                MoveLinkEntry(source, destination, true);
            }
            else
            {
                _diskProvider.MoveFolder(source, destination);
            }
        }

        private static string ReadLinkTarget(string path, bool directory)
        {
            return directory ? new DirectoryInfo(path).LinkTarget : new FileInfo(path).LinkTarget;
        }

        private static string ResolveLinkTarget(string path, string target, bool directory)
        {
            FileSystemInfo link = directory ? new DirectoryInfo(path) : new FileInfo(path);
            var resolved = link.ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path), target));
            return MediaFileRecoveryPaths.ResolveFilePath(resolved);
        }

        private static void CreateLink(string path, string target, bool directory)
        {
            if (directory)
            {
                Directory.CreateSymbolicLink(path, target);
            }
            else
            {
                File.CreateSymbolicLink(path, target);
            }
        }

        private static void VerifyLink(string path, string target, bool directory)
        {
            if (ReadLinkTarget(path, directory) != target)
            {
                throw new IOException("A symbolic link changed during the series move. Recovery was preserved: " + path);
            }
        }

        private static void DeleteLink(string path, bool directory)
        {
            // DiskProvider.DeleteFolder empties directories before removing them; never follow a link here.
            if (directory && OsInfo.IsWindows)
            {
                Directory.Delete(path, false);
            }
            else
            {
                File.Delete(path);
            }
        }

        private static void ValidateLocations(MoveJournal journal)
        {
            MediaFileRecoveryPaths.VerifyResolvedPath(journal.SourcePath, journal.ResolvedSourcePath);
            MediaFileRecoveryPaths.VerifyResolvedPath(journal.DestinationPath, journal.ResolvedDestinationPath);
        }

        private string Contained(string root, string relative)
        {
            return MediaFileRecoveryPaths.ValidateContainedPath(_diskProvider, root, Path.Combine(root, relative));
        }

        private string StagingPath(MoveJournal journal, bool allowLink = false)
        {
            var parent = Path.GetDirectoryName(journal.ResolvedDestinationPath);
            return MediaFileRecoveryPaths.ValidateContainedPath(_diskProvider, parent, Path.Combine(parent, ".sonarr-move-" + journal.OperationId), allowLink);
        }

        private string GetJournalPath(int seriesId)
        {
            var root = MediaFileRecoveryPaths.GetJournalRoot(_appFolderInfo, _diskProvider);
            return Contained(root, Path.Combine("series", seriesId + ".json"));
        }

        private string HashFile(string path)
        {
            using var stream = _diskProvider.OpenReadStream(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        private void VerifyFile(string path, string hash)
        {
            if (!_diskProvider.FileExists(path) || !string.Equals(HashFile(path), hash, StringComparison.Ordinal))
            {
                throw new IOException("A file changed or is missing during series move recovery: " + path);
            }
        }

        public class MoveJournal
        {
            public int SeriesId { get; set; }
            public string SourcePath { get; set; }
            public string DestinationPath { get; set; }
            public string ResolvedSourcePath { get; set; }
            public string ResolvedDestinationPath { get; set; }
            public string OperationId { get; set; }
            public bool AtomicMove { get; set; }
            public bool CaseOnly { get; set; }
            public bool DestinationExisted { get; set; }
            public bool Prepared { get; set; }
            public List<string> Directories { get; set; } = new List<string>();
            public List<MoveFile> Files { get; set; } = new List<MoveFile>();
            public List<MoveLink> Links { get; set; } = new List<MoveLink>();
            public MoveLink RootLink { get; set; }
        }

        public class MoveFile
        {
            public string RelativePath { get; set; }
            public string Hash { get; set; }
        }

        public class MoveLink
        {
            public string RelativePath { get; set; }
            public string OriginalTarget { get; set; }
            public string DestinationTarget { get; set; }
            public string ResolvedTarget { get; set; }
            public bool IsDirectory { get; set; }
        }
    }
}
