using System.Linq;
using NLog;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.EpisodeImport.Specifications
{
    public class SameEpisodesImportSpecification : IImportDecisionEngineSpecification
    {
        private readonly SameEpisodesSpecification _sameEpisodesSpecification;
        private readonly Logger _logger;

        public SameEpisodesImportSpecification(SameEpisodesSpecification sameEpisodesSpecification, Logger logger)
        {
            _sameEpisodesSpecification = sameEpisodesSpecification;
            _logger = logger;
        }

        public RejectionType Type => RejectionType.Permanent;

        public ImportSpecDecision IsSatisfiedBy(LocalEpisode localEpisode, DownloadClientItem downloadClientItem)
        {
            if (localEpisode.TargetQualityTrackIds is { Count: > 0 } && localEpisode.Series.QualityTracks?.Value?.Any(t =>
                !t.IsPrimary && (t.Enabled || t.TrackFiles?.Value?.Any() == true)) == true)
            {
                // Track replacements retain multi-episode files while any association still needs them.
                return ImportSpecDecision.Accept();
            }

            if (_sameEpisodesSpecification.IsSatisfiedBy(localEpisode.Episodes))
            {
                return ImportSpecDecision.Accept();
            }

            _logger.Debug("Episode file on disk contains more episodes than this file contains");
            return ImportSpecDecision.Reject(ImportRejectionReason.ExistingFileHasMoreEpisodes, "Episode file on disk contains more episodes than this file contains");
        }
    }
}
