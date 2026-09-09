using System;
using System.Collections.Generic;
using System.IO;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Serializer;

namespace NzbDrone.Core.MediaFiles
{
    internal static class MediaFileRecoveryPaths
    {
        public static string GetJournalRoot(IAppFolderInfo appFolderInfo, IDiskProvider diskProvider)
        {
            return ValidateContainedPath(diskProvider, appFolderInfo.AppDataFolder, Path.Combine(appFolderInfo.AppDataFolder, "MediaFileRecovery"));
        }

        public static void WriteJournal<T>(IDiskProvider diskProvider, string path, T journal)
        {
            var temporaryPath = path + ".new";
            var parent = Path.GetDirectoryName(Path.GetFullPath(path));
            ValidateContainedPath(diskProvider, parent, path);
            ValidateContainedPath(diskProvider, parent, temporaryPath);
            if (diskProvider.FolderExists(parent) && (diskProvider.GetFileAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Recovery receipt parent must not be a symbolic link: " + parent);
            }

            diskProvider.WriteAllText(temporaryPath, journal.ToJson());
            diskProvider.MoveFile(temporaryPath, path, true);
        }

        public static string ValidateContainedPath(IDiskProvider diskProvider, string root, string path, bool allowSymbolicLinks = false)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new IOException("Recovery receipt contains an empty path.");
            }

            var fullRoot = Path.GetFullPath(root);
            var fullPath = Path.GetFullPath(path);
            if (!fullRoot.IsParentPath(fullPath))
            {
                throw new IOException("Recovery path is outside its directory: " + path);
            }

            if (!allowSymbolicLinks)
            {
                for (var current = fullPath; !current.PathEquals(fullRoot); current = Path.GetDirectoryName(current))
                {
                    if ((diskProvider.FolderExists(current) || diskProvider.FileExists(current)) &&
                        (diskProvider.GetFileAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new IOException("Recovery storage paths must not contain symbolic links: " + current);
                    }
                }
            }

            return fullPath;
        }

        public static string ResolveFilePath(string path)
        {
            return ResolveFilePath(path, null);
        }

        public static string ResolveDirectoryPath(string path)
        {
            return ResolveDirectory(path, 0);
        }

        public static string ResolveFilePath(string path, IDictionary<string, string> resolvedDirectories)
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (resolvedDirectories == null || !resolvedDirectories.TryGetValue(directory, out var resolved))
            {
                resolved = ResolveDirectory(directory, 0);
                if (resolvedDirectories != null)
                {
                    resolvedDirectories[directory] = resolved;
                }
            }

            return Path.Combine(resolved, Path.GetFileName(fullPath));
        }

        public static void VerifyResolvedPath(string path, string expectedPath)
        {
            if (string.IsNullOrWhiteSpace(expectedPath) || !ResolveFilePath(path).PathEquals(expectedPath))
            {
                throw new IOException("Recovery path changed its symbolic link target. Its bytes and receipt are preserved: " + path);
            }
        }

        private static string ResolveDirectory(string path, int depth)
        {
            if (depth > 40)
            {
                throw new IOException("Too many symbolic links in a recovery path.");
            }

            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            var resolved = root;
            foreach (var segment in fullPath.Substring(root.Length).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                resolved = Path.Combine(resolved, segment);
                var directory = new DirectoryInfo(resolved);
                if (directory.LinkTarget != null)
                {
                    var target = directory.ResolveLinkTarget(true);
                    if (target == null)
                    {
                        throw new IOException("Unable to resolve recovery directory: " + resolved);
                    }

                    resolved = ResolveDirectory(target.FullName, depth + 1);
                }
            }

            return resolved;
        }
    }
}
