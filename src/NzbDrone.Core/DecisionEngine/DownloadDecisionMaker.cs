using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DataAugmentation.Scene;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.DecisionEngine
{
    public interface IMakeDownloadDecision
    {
        List<DownloadDecision> GetRssDecision(List<ReleaseInfo> reports, bool pushedRelease = false);
        List<DownloadDecision> GetSearchDecision(List<ReleaseInfo> reports, SearchCriteriaBase searchCriteriaBase);
    }

    public class DownloadDecisionMaker : IMakeDownloadDecision
    {
        private readonly IEnumerable<IDownloadDecisionEngineSpecification> _specifications;
        private readonly IParsingService _parsingService;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly IRemoteEpisodeAggregationService _aggregationService;
        private readonly ISceneMappingService _sceneMappingService;
        private readonly ISeriesQualityTrackService _qualityTrackService;
        private readonly IEpisodeTrackFileService _trackFileService;
        private readonly Logger _logger;

        public DownloadDecisionMaker(IEnumerable<IDownloadDecisionEngineSpecification> specifications,
                                     IParsingService parsingService,
                                     ICustomFormatCalculationService formatService,
                                     IRemoteEpisodeAggregationService aggregationService,
                                     ISceneMappingService sceneMappingService,
                                     ISeriesQualityTrackService qualityTrackService,
                                     IEpisodeTrackFileService trackFileService,
                                     Logger logger)
        {
            _specifications = specifications;
            _parsingService = parsingService;
            _formatCalculator = formatService;
            _aggregationService = aggregationService;
            _sceneMappingService = sceneMappingService;
            _qualityTrackService = qualityTrackService;
            _trackFileService = trackFileService;
            _logger = logger;
        }

        public List<DownloadDecision> GetRssDecision(List<ReleaseInfo> reports, bool pushedRelease = false)
        {
            return GetDecisions(reports, pushedRelease).ToList();
        }

        public List<DownloadDecision> GetSearchDecision(List<ReleaseInfo> reports, SearchCriteriaBase searchCriteriaBase)
        {
            return GetDecisions(reports, false, searchCriteriaBase).ToList();
        }

        private IEnumerable<DownloadDecision> GetDecisions(List<ReleaseInfo> reports, bool pushedRelease, SearchCriteriaBase searchCriteria = null)
        {
            if (reports.Any())
            {
                _logger.ProgressInfo("Processing {0} releases", reports.Count);
            }
            else
            {
                _logger.ProgressInfo("No results found");
            }

            var reportNumber = 1;
            var trackCache = new Dictionary<int, (List<SeriesQualityTrack> Tracks, List<EpisodeTrackFile> Links, Dictionary<int, string> Signatures)>();

            foreach (var report in reports)
            {
                DownloadDecision decision = null;
                _logger.ProgressTrace("Processing release {0}/{1}", reportNumber, reports.Count);
                _logger.Debug("Processing release '{0}' from '{1}'", report.Title, report.Indexer);

                try
                {
                    var parsedEpisodeInfo = Parser.Parser.ParseTitle(report.Title);

                    if (parsedEpisodeInfo == null || parsedEpisodeInfo.IsPossibleSpecialEpisode)
                    {
                        var specialEpisodeInfo = _parsingService.ParseSpecialEpisodeTitle(parsedEpisodeInfo, report.Title, report.TvdbId, report.TvRageId, report.ImdbId, searchCriteria);

                        if (specialEpisodeInfo != null)
                        {
                            parsedEpisodeInfo = specialEpisodeInfo;
                        }
                    }

                    if (parsedEpisodeInfo != null && !parsedEpisodeInfo.SeriesTitle.IsNullOrWhiteSpace())
                    {
                        var remoteEpisode = _parsingService.Map(parsedEpisodeInfo, report.TvdbId, report.TvRageId, report.ImdbId, searchCriteria);
                        remoteEpisode.Release = report;
                        remoteEpisode.ReleaseSource = GetReleaseSource(pushedRelease, searchCriteria);

                        if (remoteEpisode.Series == null)
                        {
                            var matchingTvdbId = _sceneMappingService.FindTvdbId(parsedEpisodeInfo.SeriesTitle, parsedEpisodeInfo.ReleaseTitle, parsedEpisodeInfo.SeasonNumber);

                            if (matchingTvdbId.HasValue)
                            {
                                decision = new DownloadDecision(remoteEpisode, new DownloadRejection(DownloadRejectionReason.MatchesAnotherSeries, $"{parsedEpisodeInfo.SeriesTitle} matches an alias for series with TVDB ID: {matchingTvdbId}"));
                            }
                            else
                            {
                                decision = new DownloadDecision(remoteEpisode, new DownloadRejection(DownloadRejectionReason.UnknownSeries, "Unknown Series"));
                            }
                        }
                        else if (remoteEpisode.Episodes.Empty())
                        {
                            decision = new DownloadDecision(remoteEpisode, new DownloadRejection(DownloadRejectionReason.UnknownEpisode, "Unable to identify correct episode(s) using release name and scene mappings"));
                        }
                        else
                        {
                            _aggregationService.Augment(remoteEpisode);

                            remoteEpisode.CustomFormats = _formatCalculator.ParseCustomFormat(remoteEpisode, remoteEpisode.Release.Size);
                            remoteEpisode.CustomFormatScore = remoteEpisode?.Series?.QualityProfile?.Value.CalculateCustomFormatScore(remoteEpisode.CustomFormats) ?? 0;

                            _logger.Trace("Custom Format Score of '{0}' [{1}] calculated for '{2}'", remoteEpisode.CustomFormatScore, remoteEpisode.CustomFormats?.ConcatToString(), report.Title);

                            remoteEpisode.DownloadAllowed = remoteEpisode.Episodes.Any();
                            decision = GetTrackDecisions(remoteEpisode, new ReleaseDecisionInformation(pushedRelease, searchCriteria), trackCache);
                        }
                    }

                    if (searchCriteria != null)
                    {
                        if (parsedEpisodeInfo == null)
                        {
                            parsedEpisodeInfo = new ParsedEpisodeInfo
                            {
                                Languages = LanguageParser.ParseLanguages(report.Title),
                                Quality = QualityParser.ParseQuality(report.Title)
                            };
                        }

                        if (parsedEpisodeInfo.SeriesTitle.IsNullOrWhiteSpace())
                        {
                            var remoteEpisode = new RemoteEpisode
                            {
                                Release = report,
                                ReleaseSource = GetReleaseSource(pushedRelease, searchCriteria),
                                ParsedEpisodeInfo = parsedEpisodeInfo,
                                Languages = parsedEpisodeInfo.Languages,
                            };

                            decision = new DownloadDecision(remoteEpisode, new DownloadRejection(DownloadRejectionReason.UnableToParse, "Unable to parse release"));
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger.Error(e, "Couldn't process release.");

                    var remoteEpisode = new RemoteEpisode { Release = report, ReleaseSource = GetReleaseSource(pushedRelease, searchCriteria) };

                    decision = new DownloadDecision(remoteEpisode, new DownloadRejection(DownloadRejectionReason.Error, "Unexpected error processing release"));
                }

                reportNumber++;

                if (decision != null)
                {
                    if (decision.Rejections.Any())
                    {
                        _logger.Debug("Release '{0}' from '{1}' rejected for the following reasons: {2}", report.Title, report.Indexer, string.Join(", ", decision.Rejections));
                    }
                    else
                    {
                        _logger.Debug("Release '{0}' from '{1}' accepted", report.Title, report.Indexer);
                    }

                    yield return decision;
                }
            }
        }

        private DownloadDecision GetTrackDecisions(RemoteEpisode remoteEpisode, ReleaseDecisionInformation information, Dictionary<int, (List<SeriesQualityTrack> Tracks, List<EpisodeTrackFile> Links, Dictionary<int, string> Signatures)> cache)
        {
            if (!cache.TryGetValue(remoteEpisode.Series.Id, out var state))
            {
                var enabledTracks = _qualityTrackService.GetEnabledTracks(remoteEpisode.Series.Id);
                state = (
                    enabledTracks,
                    _trackFileService.GetForSeries(remoteEpisode.Series.Id),
                    enabledTracks?.ToDictionary(t => t.Id, t => QualityTrackSnapshot.ProfileSignature(t.QualityProfile.Value)));
                cache.Add(remoteEpisode.Series.Id, state);
            }

            var tracks = state.Tracks;
            var requested = remoteEpisode.Release.TargetQualityTrackIds;

            if (tracks == null || tracks.Count == 0)
            {
                return GetDecisionForReport(remoteEpisode, information);
            }

            if (requested != null)
            {
                tracks = tracks.Where(t => requested.Contains(t.Id)).ToList();
            }

            if (tracks.Count == 0)
            {
                remoteEpisode.TargetQualityTrackIds = [];
                remoteEpisode.DownloadAllowed = false;
                return new DownloadDecision(remoteEpisode, new DownloadRejection(DownloadRejectionReason.Unknown, "Selected quality profiles are no longer enabled"));
            }

            var decisions = tracks.Select(track => GetDecisionForReport(QualityTrackSnapshot.Create(remoteEpisode, track, state.Links, state.Signatures[track.Id]), information)).ToList();
            var accepted = decisions.Where(d => d.Approved).ToList();
            var eligible = accepted.Any() ? accepted : decisions.Where(d => d.TemporarilyRejected).ToList();
            remoteEpisode.TargetQualityTrackIds = eligible.SelectMany(d => d.RemoteEpisode.TargetQualityTrackIds).Distinct().ToList();
            remoteEpisode.TargetQualityTrackSignatures = eligible.SelectMany(d => d.RemoteEpisode.TargetQualityTrackSignatures).ToDictionary(p => p.Key, p => p.Value);
            var rejections = accepted.Any() ? [] : (eligible.Any() ? eligible : decisions).SelectMany(d => d.Rejections).ToArray();

            return new DownloadDecision(remoteEpisode, rejections) { QualityTrackDecisions = decisions };
        }

        private DownloadDecision GetDecisionForReport(RemoteEpisode remoteEpisode, ReleaseDecisionInformation information)
        {
            var reasons = Array.Empty<DownloadRejection>();

            foreach (var specifications in _specifications.GroupBy(v => v.Priority).OrderBy(v => v.Key))
            {
                reasons = specifications.Select(c => EvaluateSpec(c, remoteEpisode, information))
                                        .Where(c => c != null)
                                        .ToArray();

                if (reasons.Any())
                {
                    break;
                }
            }

            return new DownloadDecision(remoteEpisode, reasons.ToArray());
        }

        private DownloadRejection EvaluateSpec(IDownloadDecisionEngineSpecification spec, RemoteEpisode remoteEpisode, ReleaseDecisionInformation information)
        {
            try
            {
                var result = spec.IsSatisfiedBy(remoteEpisode, information);

                if (!result.Accepted)
                {
                    return new DownloadRejection(result.Reason, result.Message, spec.Type);
                }
            }
            catch (Exception e)
            {
                e.Data.Add("report", remoteEpisode.Release.ToJson());
                e.Data.Add("parsed", remoteEpisode.ParsedEpisodeInfo.ToJson());
                _logger.Error(e, "Couldn't evaluate decision on {0}", remoteEpisode.Release.Title);
                return new DownloadRejection(DownloadRejectionReason.DecisionError, $"{spec.GetType().Name}: {e.Message}");
            }

            return null;
        }

        private ReleaseSourceType GetReleaseSource(bool pushedRelease, SearchCriteriaBase searchCriteria = null)
        {
            if (searchCriteria == null)
            {
                return pushedRelease ? ReleaseSourceType.ReleasePush : ReleaseSourceType.Rss;
            }

            if (searchCriteria.InteractiveSearch)
            {
                return ReleaseSourceType.InteractiveSearch;
            }
            else if (searchCriteria.UserInvokedSearch)
            {
                return ReleaseSourceType.UserInvokedSearch;
            }
            else
            {
                return ReleaseSourceType.Search;
            }
        }
    }
}
