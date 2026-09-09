using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using FluentValidation;
using FluentValidation.Results;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Tv
{
    public interface ISeriesQualityTrackRepository
    {
        IEnumerable<SeriesQualityTrack> All();
        SeriesQualityTrack Get(int trackId);
        List<SeriesQualityTrack> GetForSeries(int seriesId);
        bool IsProfileInUse(int profileId);
        void ValidateProfiles(int seriesId, int primaryId, IEnumerable<int> additionalIds);
    }

    public class SeriesQualityTrackRepository : BasicRepository<SeriesQualityTrack>, ISeriesQualityTrackRepository
    {
        public SeriesQualityTrackRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public List<SeriesQualityTrack> GetForSeries(int seriesId)
        {
            return Query(t => t.SeriesId == seriesId).OrderByDescending(t => t.IsPrimary).ThenBy(t => t.Id).ToList();
        }

        public bool IsProfileInUse(int profileId)
        {
            using var connection = _database.OpenConnection();
            return connection.ExecuteScalar<bool>(
                @"SELECT EXISTS (
                SELECT 1 FROM ""SeriesQualityTracks"" t
                WHERE t.""QualityProfileId"" = @profileId AND (t.""Enabled"" = true OR EXISTS
                    (SELECT 1 FROM ""EpisodeTrackFiles"" l WHERE l.""TrackId"" = t.""Id"")))",
                new { profileId });
        }

        public void ValidateProfiles(int seriesId, int primaryId, IEnumerable<int> additionalIds)
        {
            using var connection = _database.OpenConnection();
            ValidateProfiles(connection, null, _database.DatabaseType, seriesId, primaryId, additionalIds);
        }

        internal static List<int> ValidateProfiles(IDbConnection connection, IDbTransaction transaction, DatabaseType databaseType, int seriesId, int primaryId, IEnumerable<int> additionalIds)
        {
            var additional = additionalIds?.ToList() ?? connection.Query<int>(
                "SELECT \"QualityProfileId\" FROM \"SeriesQualityTracks\" WHERE \"SeriesId\" = @seriesId AND \"IsPrimary\" = false AND \"Enabled\" = true",
                new { seriesId },
                transaction).ToList();

            if (additional.Contains(primaryId) || additional.Count != additional.Distinct().Count())
            {
                throw new ValidationException(new[] { new ValidationFailure("AdditionalQualityProfileIds", "Each enabled version must use a different quality profile.") });
            }

            var selected = additional.Append(primaryId).ToList();
            var condition = databaseType == DatabaseType.PostgreSQL ? "= ANY(@selected)" : "IN @selected";
            var existing = connection.Query<int>($"SELECT \"Id\" FROM \"QualityProfiles\" WHERE \"Id\" {condition}", new { selected = selected.ToArray() }, transaction).ToHashSet();
            if (selected.Any(id => !existing.Contains(id)))
            {
                throw new ValidationException(new[] { new ValidationFailure("AdditionalQualityProfileIds", "One or more quality profiles do not exist.") });
            }

            return additional;
        }

        internal static void Save(IDbConnection connection, IDbTransaction transaction, DatabaseType databaseType, Series series, List<int> additional)
        {
            var tracks = connection.Query<SeriesQualityTrack>("SELECT * FROM \"SeriesQualityTracks\" WHERE \"SeriesId\" = @Id", series, transaction).ToList();
            var primary = tracks.SingleOrDefault(t => t.IsPrimary);

            // Disable removed selections first so an explicitly removed additional profile can become primary.
            foreach (var track in tracks.Where(t => !t.IsPrimary && t.Enabled && !additional.Contains(t.QualityProfileId)))
            {
                connection.Execute("UPDATE \"SeriesQualityTracks\" SET \"Enabled\" = false WHERE \"Id\" = @Id", track, transaction);
                track.Enabled = false;
            }

            if (primary == null)
            {
                primary = new SeriesQualityTrack { SeriesId = series.Id, QualityProfileId = series.QualityProfileId, IsPrimary = true, Enabled = true };
                InsertTrack(connection, transaction, databaseType, primary);
            }
            else if (primary.QualityProfileId != series.QualityProfileId)
            {
                connection.Execute(
                    "UPDATE \"SeriesQualityTracks\" SET \"QualityProfileId\" = @QualityProfileId WHERE \"Id\" = @Id",
                    new { primary.Id, series.QualityProfileId },
                    transaction);
            }

            foreach (var profileId in additional)
            {
                var track = tracks.Where(t => !t.IsPrimary && t.QualityProfileId == profileId).OrderByDescending(t => t.Enabled).ThenBy(t => t.Id).FirstOrDefault();
                if (track == null)
                {
                    InsertTrack(connection, transaction, databaseType, new SeriesQualityTrack { SeriesId = series.Id, QualityProfileId = profileId, Enabled = true });
                }
                else if (!track.Enabled)
                {
                    connection.Execute("UPDATE \"SeriesQualityTracks\" SET \"Enabled\" = true WHERE \"Id\" = @Id", track, transaction);
                }
            }
        }

        private static void InsertTrack(IDbConnection connection, IDbTransaction transaction, DatabaseType databaseType, SeriesQualityTrack track)
        {
            var sql = "INSERT INTO \"SeriesQualityTracks\" (\"SeriesId\", \"QualityProfileId\", \"IsPrimary\", \"Enabled\") VALUES (@SeriesId, @QualityProfileId, @IsPrimary, @Enabled)";
            sql += databaseType == DatabaseType.PostgreSQL ? " RETURNING \"Id\"" : "; SELECT last_insert_rowid()";
            track.Id = connection.ExecuteScalar<int>(sql, track, transaction);
        }
    }
}
