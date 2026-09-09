using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Extras
{
    public class ExistingExtraFileEpisodeMatcher
    {
        private readonly List<EpisodeFile> _files;
        private readonly Dictionary<int, HashSet<int>> _episodesByFile;

        public ExistingExtraFileEpisodeMatcher(List<EpisodeFile> files, List<EpisodeTrackFile> links)
        {
            _files = files;
            _episodesByFile = links.GroupBy(l => l.EpisodeFileId)
                .ToDictionary(g => g.Key, g => g.Select(l => l.EpisodeId).ToHashSet());
        }

        public EpisodeFile Find(LocalEpisode localEpisode, int? importedEpisodeFileId)
        {
            var episodeIds = localEpisode.Episodes.Select(e => e.Id).ToHashSet();
            if (episodeIds.Count == 0)
            {
                return null;
            }

            var candidates = _files.Where(f => _episodesByFile.TryGetValue(f.Id, out var ids) && episodeIds.IsSubsetOf(ids)).ToList();
            if (importedEpisodeFileId.HasValue)
            {
                candidates = candidates.Where(f => f.Id == importedEpisodeFileId.Value).ToList();
            }

            // Preserve existing generic sidecar names when there is only one possible physical owner.
            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            var matches = candidates.Select(f => new
            {
                File = f,
                Length = MatchLength(localEpisode, f)
            }).Where(m => m.Length > 0).ToList();

            if (matches.Count == 0)
            {
                return null;
            }

            var longest = matches.Max(m => m.Length);
            var best = matches.Where(m => m.Length == longest).ToList();
            return best.Count == 1 ? best[0].File : null;
        }

        private static int MatchLength(LocalEpisode localEpisode, EpisodeFile file)
        {
            var filePath = Path.Combine(localEpisode.Series.Path, file.RelativePath);
            if (!Path.GetDirectoryName(filePath).PathEquals(Path.GetDirectoryName(localEpisode.Path)))
            {
                return 0;
            }

            var sidecarStem = Path.GetFileNameWithoutExtension(localEpisode.Path);
            var stems = new[]
            {
                Path.GetFileNameWithoutExtension(file.RelativePath),
                Path.GetFileNameWithoutExtension(file.OriginalFilePath),
                file.SceneName
            };

            return stems.Where(s => !string.IsNullOrEmpty(s) && sidecarStem.StartsWith(s, StringComparison.OrdinalIgnoreCase) &&
                    (sidecarStem.Length == s.Length || ".-_ ".Contains(sidecarStem[s.Length])))
                .Select(s => s.Length).DefaultIfEmpty().Max();
        }
    }
}
