using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaFiles
{
    public interface IEpisodeTrackFileRepository
    {
        IEnumerable<EpisodeTrackFile> All();
        List<EpisodeTrackFile> GetForEpisode(int episodeId);
        List<EpisodeTrackFile> GetForSeries(int seriesId);
        List<EpisodeTrackFile> GetForFile(int fileId);
        List<int> ReplaceLinks(int seriesId, List<EpisodeTrackFile> links);
        List<int> ImportFile(EpisodeFile file, List<EpisodeTrackFile> links);
        List<int> UpdateFile(EpisodeFile file, List<EpisodeTrackFile> links, bool replaceAllFileLinks = false);
        void RemoveFile(int fileId);
        void DeleteFile(int fileId);
        bool IsFileReferenced(int fileId);
        Dictionary<int, int> GetFileCountsByTrack();
    }

    public class EpisodeTrackFileRepository : BasicRepository<EpisodeTrackFile>, IEpisodeTrackFileRepository
    {
        private readonly IEventAggregator _eventAggregator;

        public EpisodeTrackFileRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
            _eventAggregator = eventAggregator;
        }

        public List<EpisodeTrackFile> GetForEpisode(int episodeId) => Query(l => l.EpisodeId == episodeId);

        public List<EpisodeTrackFile> GetForSeries(int seriesId) => Query(Builder().Where(
            "\"EpisodeTrackFiles\".\"TrackId\" IN (SELECT \"Id\" FROM \"SeriesQualityTracks\" WHERE \"SeriesId\" = @seriesId)", new { seriesId }));

        public List<EpisodeTrackFile> GetForFile(int fileId) => Query(l => l.EpisodeFileId == fileId);

        public bool IsFileReferenced(int fileId)
        {
            using var connection = _database.OpenConnection();
            return connection.ExecuteScalar<bool>("SELECT EXISTS (SELECT 1 FROM \"EpisodeTrackFiles\" WHERE \"EpisodeFileId\" = @fileId)", new { fileId });
        }

        public Dictionary<int, int> GetFileCountsByTrack()
        {
            using var connection = _database.OpenConnection();
            return connection.Query<KeyValuePair<int, int>>("SELECT \"TrackId\" AS Key, CAST(COUNT(DISTINCT \"EpisodeFileId\") AS INTEGER) AS Value FROM \"EpisodeTrackFiles\" GROUP BY \"TrackId\"").ToDictionary(p => p.Key, p => p.Value);
        }

        public List<int> ReplaceLinks(int seriesId, List<EpisodeTrackFile> links)
        {
            using var operationLock = MediaFileOperationLock.Acquire(new[] { seriesId });
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            LockSeries(connection, transaction, seriesId);
            var displaced = ReplaceLinks(connection, transaction, seriesId, links);
            transaction.Commit();
            return displaced;
        }

        public List<int> ImportFile(EpisodeFile file, List<EpisodeTrackFile> links)
        {
            if (file.Id != 0 || links.Count == 0)
            {
                throw new ArgumentException("A new episode file must have at least one target.", nameof(file));
            }

            using var operationLock = MediaFileOperationLock.Acquire(new[] { file.SeriesId });
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            LockSeries(connection, transaction, file.SeriesId);
            var repository = new BasicRepository<EpisodeFile>(_database, _eventAggregator);
            repository.Insert(connection, transaction, file);

            try
            {
                foreach (var link in links)
                {
                    link.EpisodeFileId = file.Id;
                }

                var displaced = ReplaceLinks(connection, transaction, file.SeriesId, links);
                transaction.Commit();
                file.TrackFiles = links;
                return displaced;
            }
            catch
            {
                file.Id = 0;
                throw;
            }
        }

        public void RemoveFile(int fileId) => RemoveFile(fileId, false);

        public List<int> UpdateFile(EpisodeFile file, List<EpisodeTrackFile> links, bool replaceAllFileLinks = false)
        {
            using var operationLock = MediaFileOperationLock.Acquire(new[] { file.SeriesId });
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            LockSeries(connection, transaction, file.SeriesId);
            var storedSeriesId = connection.QuerySingleOrDefault<int?>("SELECT \"SeriesId\" FROM \"EpisodeFiles\" WHERE \"Id\" = @Id", file, transaction);
            if (storedSeriesId != file.SeriesId)
            {
                throw new ArgumentException("The existing episode file must belong to the same series.", nameof(file));
            }

            foreach (var link in links)
            {
                link.EpisodeFileId = file.Id;
            }

            ValidateLinks(connection, transaction, file.SeriesId, links);

            var previousEpisodes = new List<int>();
            if (replaceAllFileLinks)
            {
                previousEpisodes = connection.Query<int>("SELECT DISTINCT \"EpisodeId\" FROM \"EpisodeTrackFiles\" WHERE \"EpisodeFileId\" = @Id", file, transaction).ToList();
                connection.Execute("DELETE FROM \"EpisodeTrackFiles\" WHERE \"EpisodeFileId\" = @Id", file, transaction);
            }

            var repository = new BasicRepository<EpisodeFile>(_database, _eventAggregator);
            repository.Update(connection, transaction, file);
            var displaced = ReplaceLinks(connection, transaction, file.SeriesId, links, true);
            UpdateProjections(connection, transaction, _database.DatabaseType, previousEpisodes);
            transaction.Commit();
            file.TrackFiles = GetForFile(file.Id);
            return displaced;
        }

        public void DeleteFile(int fileId) => RemoveFile(fileId, true);

        private void RemoveFile(int fileId, bool deleteFile)
        {
            using var connection = _database.OpenConnection();
            var seriesId = connection.ExecuteScalar<int>("SELECT \"SeriesId\" FROM \"EpisodeFiles\" WHERE \"Id\" = @fileId", new { fileId });
            using var operationLock = MediaFileOperationLock.Acquire(new[] { seriesId });
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            LockSeries(connection, transaction, seriesId);
            var episodes = connection.Query<int>("SELECT DISTINCT \"EpisodeId\" FROM \"EpisodeTrackFiles\" WHERE \"EpisodeFileId\" = @fileId", new { fileId }, transaction).ToList();
            connection.Execute("DELETE FROM \"EpisodeTrackFiles\" WHERE \"EpisodeFileId\" = @fileId", new { fileId }, transaction);
            UpdateProjections(connection, transaction, _database.DatabaseType, episodes);
            if (deleteFile)
            {
                connection.Execute("DELETE FROM \"EpisodeFiles\" WHERE \"Id\" = @fileId", new { fileId }, transaction);
            }

            transaction.Commit();
        }

        private static void ValidateLinks(IDbConnection connection, IDbTransaction transaction, int seriesId, List<EpisodeTrackFile> links)
        {
            if (links.GroupBy(l => (l.EpisodeId, l.TrackId)).Any(g => g.Count() > 1))
            {
                throw new ArgumentException("An episode may have only one file for each version.", nameof(links));
            }

            foreach (var link in links)
            {
                var valid = connection.ExecuteScalar<bool>(
                    @"SELECT EXISTS (
                    SELECT 1 FROM ""Episodes"" e, ""SeriesQualityTracks"" t, ""EpisodeFiles"" f
                    WHERE e.""Id"" = @EpisodeId AND t.""Id"" = @TrackId AND f.""Id"" = @EpisodeFileId
                    AND e.""SeriesId"" = @seriesId AND t.""SeriesId"" = @seriesId AND f.""SeriesId"" = @seriesId
                    AND (t.""Enabled"" = true OR EXISTS
                        (SELECT 1 FROM ""EpisodeTrackFiles"" l WHERE l.""EpisodeId"" = @EpisodeId
                         AND l.""TrackId"" = @TrackId AND l.""EpisodeFileId"" = @EpisodeFileId)))",
                    new { link.EpisodeId, link.TrackId, link.EpisodeFileId, seriesId },
                    transaction);
                if (!valid)
                {
                    throw new ArgumentException("Episode, version and file must belong to the same series and new targets must be enabled.", nameof(links));
                }
            }
        }

        private List<int> ReplaceLinks(IDbConnection connection, IDbTransaction transaction, int seriesId, List<EpisodeTrackFile> links, bool validated = false)
        {
            if (!validated)
            {
                ValidateLinks(connection, transaction, seriesId, links);
            }

            var displaced = new HashSet<int>();
            foreach (var link in links)
            {
                var previous = connection.QuerySingleOrDefault<EpisodeTrackFile>(
                    "SELECT * FROM \"EpisodeTrackFiles\" WHERE \"EpisodeId\" = @EpisodeId AND \"TrackId\" = @TrackId", link, transaction);
                if (previous == null)
                {
                    Insert(connection, transaction, link);
                }
                else
                {
                    link.Id = previous.Id;
                    if (previous.EpisodeFileId != link.EpisodeFileId)
                    {
                        displaced.Add(previous.EpisodeFileId);
                        Update(connection, transaction, link);
                    }
                }
            }

            UpdateProjections(connection, transaction, _database.DatabaseType, links.Select(l => l.EpisodeId).Distinct().ToList());
            return displaced.OrderBy(id => id).ToList();
        }

        private void LockSeries(IDbConnection connection, IDbTransaction transaction, int seriesId)
        {
            if (_database.DatabaseType == DatabaseType.PostgreSQL)
            {
                connection.Query<int>("SELECT \"Id\" FROM \"Series\" WHERE \"Id\" = @seriesId FOR UPDATE", new { seriesId }, transaction).ToList();
            }
        }

        internal static void UpdateProjections(IDbConnection connection, IDbTransaction transaction, DatabaseType databaseType, List<int> episodeIds)
        {
            if (episodeIds.Count == 0)
            {
                return;
            }

            var condition = databaseType == DatabaseType.PostgreSQL ? "= ANY(@episodeIds)" : "IN @episodeIds";

            // Legacy consumers see the primary file, or the first retained version by stable track ID.
            connection.Execute(
                $@"UPDATE ""Episodes"" SET ""EpisodeFileId"" = COALESCE(
                (SELECT l.""EpisodeFileId"" FROM ""EpisodeTrackFiles"" l
                 JOIN ""SeriesQualityTracks"" t ON t.""Id"" = l.""TrackId""
                 WHERE l.""EpisodeId"" = ""Episodes"".""Id""
                 ORDER BY t.""IsPrimary"" DESC, t.""Id"" ASC LIMIT 1), 0)
                 WHERE ""Id"" {condition}",
                new { episodeIds = episodeIds.ToArray() },
                transaction);
        }
    }
}
