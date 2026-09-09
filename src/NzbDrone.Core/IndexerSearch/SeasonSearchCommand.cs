using System.Collections.Generic;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.IndexerSearch
{
    public class SeasonSearchCommand : Command
    {
        public List<int> TargetQualityTrackIds { get; set; }

        public int SeriesId { get; set; }
        public int SeasonNumber { get; set; }

        public override bool SendUpdatesToClient => true;
    }
}
