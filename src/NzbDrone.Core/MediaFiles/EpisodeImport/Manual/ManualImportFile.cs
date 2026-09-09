using System;
using System.Collections.Generic;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.MediaFiles.EpisodeImport.Manual
{
    public class ManualImportFile : IEquatable<ManualImportFile>
    {
        public string Path { get; set; }
        public string FolderName { get; set; }
        public int SeriesId { get; set; }
        public List<int> EpisodeIds { get; set; } = [];
        public List<int> TargetQualityTrackIds { get; set; }
        public int? EpisodeFileId { get; set; }
        public QualityModel Quality { get; set; } = new();
        public List<Language> Languages { get; set; } = [];
        public string ReleaseGroup { get; set; }
        public int IndexerFlags { get; set; }
        public ReleaseType ReleaseType { get; set; }
        public string DownloadId { get; set; }

        public bool Equals(ManualImportFile other)
        {
            if (other == null)
            {
                return false;
            }

            return Path.PathEquals(other.Path) &&
                   (TargetQualityTrackIds == null
                       ? other.TargetQualityTrackIds == null
                       : other.TargetQualityTrackIds != null && new HashSet<int>(TargetQualityTrackIds).SetEquals(other.TargetQualityTrackIds));
        }

        public override bool Equals(object obj)
        {
            if (obj == null)
            {
                return false;
            }

            if (obj.GetType() != GetType())
            {
                return false;
            }

            return Equals((ManualImportFile)obj);
        }

        public override int GetHashCode()
        {
            return Path != null ? Path.GetHashCode() : 0;
        }
    }
}
