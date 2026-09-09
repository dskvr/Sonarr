using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.DecisionEngine
{
    public static class QualityTrackSnapshot
    {
        public static RemoteEpisode Create(RemoteEpisode source, SeriesQualityTrack track, IEnumerable<EpisodeTrackFile> links, string signature = null)
        {
            var result = source.Clone();
            result.Series = CreateSeries(source.Series, track);
            result.Episodes = CreateEpisodes(source.Episodes, result.Series, track.Id, links);
            result.TargetQualityTrackIds = [track.Id];
            result.TargetQualityTrackSignatures = new Dictionary<int, string> { [track.Id] = signature ?? ProfileSignature(track.QualityProfile.Value) };
            result.CustomFormatScore = track.QualityProfile.Value.CalculateCustomFormatScore(source.CustomFormats);
            return result;
        }

        public static Series CreateSeries(Series source, SeriesQualityTrack track)
        {
            var result = source.Clone();
            result.QualityProfileId = track.QualityProfileId;
            result.QualityProfile = track.QualityProfile;
            return result;
        }

        public static List<Episode> CreateEpisodes(IEnumerable<Episode> source, Series series, int trackId, IEnumerable<EpisodeTrackFile> links)
        {
            var files = links.Where(l => l.TrackId == trackId).ToDictionary(l => l.EpisodeId);
            return source.Select(episode =>
            {
                var result = episode.Clone();
                files.TryGetValue(episode.Id, out var link);
                result.Series = series;
                result.EpisodeFileId = link?.EpisodeFileId ?? 0;
                result.EpisodeFile = link?.EpisodeFile;
                return result;
            }).ToList();
        }

        public static bool IsMissing(Episode episode, Series series = null)
        {
            var tracks = (series ?? episode.Series)?.QualityTracks?.Value?.Where(t => t.Enabled).ToList();

            if (tracks == null || tracks.Count == 0)
            {
                return !episode.HasFile;
            }

            var links = episode.TrackFiles?.Value ?? [];
            return tracks.Any(t => links.All(l => l.TrackId != t.Id));
        }

        public static List<DownloadDecision> FilterDecisions(List<DownloadDecision> decisions, List<int> targetIds)
        {
            if (targetIds == null)
            {
                return decisions;
            }

            return decisions.SelectMany(d => d.QualityTrackDecisions.Count == 0 ? [d] : d.QualityTrackDecisions)
                .Where(d => GetTargets(d.RemoteEpisode).Intersect(targetIds).Any()).ToList();
        }

        public static bool TargetsOverlap(RemoteEpisode first, RemoteEpisode second)
        {
            return GetTargets(first).Intersect(GetTargets(second)).Any();
        }

        public static IEnumerable<int> GetTargets(RemoteEpisode episode)
        {
            return episode.TargetQualityTrackIds ?? LegacyTargets(episode.Series);
        }

        public static List<int> LegacyTargets(Series series)
        {
            var primary = series?.QualityTracks?.Value?.SingleOrDefault(t => t.IsPrimary);
            return primary == null ? [0] : [primary.Id];
        }

        public static string ProfileSignature(QualityProfile profile)
        {
            var criteria = new
            {
                profile.UpgradeAllowed,
                profile.Cutoff,
                profile.MinFormatScore,
                profile.CutoffFormatScore,
                profile.MinUpgradeFormatScore,
                Items = profile.Items?.Select(QualityCriteria),
                Formats = profile.FormatItems.OrderBy(f => f.Format.Id).Select(f => new
                {
                    f.Format.Id,
                    f.Score,
                    Specifications = f.Format.Specifications?.Select(SpecificationCriteria).OrderBy(s => s, StringComparer.Ordinal)
                })
            };

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(STJson.ToJson(criteria))));
        }

        public static void CaptureSignatures(RemoteEpisode remote)
        {
            if (remote.TargetQualityTrackIds == null)
            {
                return;
            }

            var signatures = remote.TargetQualityTrackSignatures == null
                ? new Dictionary<int, string>()
                : new Dictionary<int, string>(remote.TargetQualityTrackSignatures);

            foreach (var targetId in remote.TargetQualityTrackIds)
            {
                if (signatures.ContainsKey(targetId))
                {
                    continue;
                }

                var track = remote.Series.QualityTracks?.Value?.SingleOrDefault(t => t.Id == targetId);
                var profile = track?.QualityProfile?.Value ?? (remote.TargetQualityTrackIds.Count == 1 ? remote.Series.QualityProfile?.Value : null);

                if (profile != null)
                {
                    signatures[targetId] = ProfileSignature(profile);
                }
            }

            remote.TargetQualityTrackSignatures = signatures;
        }

        public static Dictionary<int, string> ReadSignatures(IDictionary<string, string> data)
        {
            if (data == null || !data.TryGetValue("qualityTrackSignatures", out var value))
            {
                return null;
            }

            if (value == null)
            {
                return new Dictionary<int, string>();
            }

            try
            {
                return STJson.Deserialize<Dictionary<int, string>>(value) ?? new Dictionary<int, string>();
            }
            catch (JsonException)
            {
                return new Dictionary<int, string>();
            }
        }

        private static object QualityCriteria(QualityProfileQualityItem item)
        {
            return new
            {
                item.Id,
                Quality = item.Quality?.Id,
                item.Allowed,
                item.MinSize,
                item.MaxSize,
                item.PreferredSize,
                Items = item.Items.Select(QualityCriteria)
            };
        }

        private static string SpecificationCriteria(ICustomFormatSpecification specification)
        {
            using var document = JsonDocument.Parse(STJson.ToJson(specification));
            var properties = document.RootElement.EnumerateObject()
                .Where(p => p.Name != "name" && p.Name != "infoLink" && p.Name != "implementationName" && p.Name != "order")
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToDictionary(p => p.Name, p => p.Value.Clone());
            return specification.GetType().FullName + STJson.ToJson(properties);
        }

        public static List<int> ReadTargets(IDictionary<string, string> data, string key = "qualityTrackIds")
        {
            if (data == null || !data.TryGetValue(key, out var value))
            {
                return null;
            }

            if (value == null)
            {
                return [];
            }

            try
            {
                var targets = STJson.Deserialize<List<int>>(value);
                return targets == null || targets.Any(id => id <= 0) ? [] : targets.Distinct().ToList();
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }
}
