using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using Dapper;
using FluentValidation;
using FluentValidation.Results;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Tv
{
    public interface ISeriesRepository : IBasicRepository<Series>
    {
        bool SeriesPathExists(string path);
        Series FindByTitle(string cleanTitle);
        Series FindByTitle(string cleanTitle, int year);
        List<Series> FindByTitleInexact(string cleanTitle);
        Series FindByTvdbId(int tvdbId);
        Series FindByTvRageId(int tvRageId);
        Series FindByImdbId(string imdbId);
        Series FindByPath(string path);
        Dictionary<int, int> AllSeriesTvdbIds();
        Dictionary<int, string> AllSeriesPaths();
        Dictionary<int, List<int>> AllSeriesTags();
        Dictionary<int, int> AllSeriesQualityProfiles();
        bool HasPathConflict(int seriesId, string path);
        Series UpdatePath(int seriesId, string path);
    }

    public class SeriesRepository : BasicRepository<Series>, ISeriesRepository
    {
        public SeriesRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public override Series Insert(Series model)
        {
            InsertMany(new[] { model });
            ModelCreated(model);
            return model;
        }

        public override void InsertMany(IList<Series> models)
        {
            if (models.Any(s => s.Id != 0))
            {
                throw new InvalidOperationException("Can't insert model with existing ID != 0");
            }

            using var operationLock = MediaFileOperationLock.AcquireAll();
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            ValidatePaths(connection, transaction, models);
            var selections = models.Select(s => SeriesQualityTrackRepository.ValidateProfiles(connection, transaction, _database.DatabaseType, s.Id, s.QualityProfileId, s.AdditionalQualityProfileIds)).ToList();
            for (var i = 0; i < models.Count; i++)
            {
                Insert(connection, transaction, models[i]);
                SeriesQualityTrackRepository.Save(connection, transaction, _database.DatabaseType, models[i], selections[i]);
            }

            transaction.Commit();
            RefreshTracks(models);
        }

        public override Series Update(Series model)
        {
            UpdateMany(new[] { model });
            ModelUpdated(model);
            return model;
        }

        public override void UpdateMany(IList<Series> models)
        {
            if (models.Any(s => s.Id == 0))
            {
                throw new InvalidOperationException("Can't update model with ID 0");
            }

            using var operationLock = AcquireUpdateLock(models);
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            if (models.Count > 0)
            {
                var ids = models.Select(s => s.Id).Distinct().ToArray();
                var sql = _database.DatabaseType == DatabaseType.PostgreSQL
                    ? "SELECT \"Id\" FROM \"Series\" WHERE \"Id\" = ANY(@ids) ORDER BY \"Id\" FOR UPDATE"
                    : "SELECT \"Id\" FROM \"Series\" WHERE \"Id\" IN @ids";
                var existing = connection.Query<int>(sql, new { ids }, transaction).ToHashSet();
                if (existing.Count != ids.Length)
                {
                    throw new ModelNotFoundException(typeof(Series), ids.First(id => !existing.Contains(id)));
                }
            }

            var selections = models.Select(s => SeriesQualityTrackRepository.ValidateProfiles(connection, transaction, _database.DatabaseType, s.Id, s.QualityProfileId, s.AdditionalQualityProfileIds)).ToList();
            ValidatePaths(connection, transaction, models);
            for (var i = 0; i < models.Count; i++)
            {
                Update(connection, transaction, models[i]);
                SeriesQualityTrackRepository.Save(connection, transaction, _database.DatabaseType, models[i], selections[i]);
            }

            transaction.Commit();
            RefreshTracks(models);
        }

        private IDisposable AcquireUpdateLock(IList<Series> models)
        {
            while (true)
            {
                var paths = AllSeriesPaths();
                var changingPath = models.Any(s => !paths.TryGetValue(s.Id, out var path) || !s.Path.PathEquals(path));
                var scope = changingPath ? MediaFileOperationLock.AcquireAll() : MediaFileOperationLock.Acquire(models.Select(s => s.Id));
                try
                {
                    paths = AllSeriesPaths();
                    if (changingPath || models.All(s => paths.TryGetValue(s.Id, out var path) && s.Path.PathEquals(path)))
                    {
                        return scope;
                    }
                }
                catch
                {
                    scope.Dispose();
                    throw;
                }

                scope.Dispose();
            }
        }

        private static void ValidatePaths(IDbConnection connection, IDbTransaction transaction, IList<Series> models)
        {
            var existing = connection.Query<KeyValuePair<int, string>>("SELECT \"Id\" AS Key, \"Path\" AS Value FROM \"Series\"", transaction: transaction).ToDictionary(p => p.Key, p => p.Value);
            var updatedIds = models.Where(s => s.Id != 0).Select(s => s.Id).ToHashSet();
            for (var index = 0; index < models.Count; index++)
            {
                var model = models[index];
                if (existing.TryGetValue(model.Id, out var previous) && model.Path.PathEquals(previous))
                {
                    continue;
                }

                if (existing.Any(p => !updatedIds.Contains(p.Key) && PathsOverlap(model.Path, p.Value)) ||
                    models.Where((_, otherIndex) => index != otherIndex).Any(other => PathsOverlap(model.Path, other.Path)))
                {
                    throw new ValidationException(new[] { new ValidationFailure("Path", "Another series already uses this folder or an overlapping folder.") });
                }
            }
        }

        internal static bool PathsOverlap(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
            {
                return false;
            }

            first = Path.GetFullPath(first);
            second = Path.GetFullPath(second);
            if (first.PathEquals(second) || first.IsParentPath(second) || second.IsParentPath(first))
            {
                return true;
            }

            first = MediaFileRecoveryPaths.ResolveFilePath(first);
            second = MediaFileRecoveryPaths.ResolveFilePath(second);
            return first.PathEquals(second) || first.IsParentPath(second) || second.IsParentPath(first);
        }

        public bool HasPathConflict(int seriesId, string path)
        {
            return AllSeriesPaths().Any(p => p.Key != seriesId && PathsOverlap(path, p.Value));
        }

        public Series UpdatePath(int seriesId, string path)
        {
            using var operationLock = MediaFileOperationLock.AcquireAll();
            var series = Get(seriesId);
            series.Path = path;
            using (var connection = _database.OpenConnection())
            {
                ValidatePaths(connection, null, new[] { series });
            }

            SetFields(series, s => s.Path);
            return series;
        }

        private void RefreshTracks(IList<Series> models)
        {
            var ids = models.Select(s => s.Id).ToList();
            if (ids.Count == 0)
            {
                return;
            }

            var tracks = _database.Query<SeriesQualityTrack>(new SqlBuilder(_database.DatabaseType).Where<SeriesQualityTrack>(t => ids.Contains(t.SeriesId))).ToLookup(t => t.SeriesId);
            foreach (var series in models)
            {
                series.QualityTracks = tracks[series.Id].ToList();
            }
        }

        public override void DeleteMany(IEnumerable<int> ids)
        {
            var seriesIds = ids.Distinct().ToList();
            if (seriesIds.Count == 0)
            {
                return;
            }

            var condition = _database.DatabaseType == DatabaseType.PostgreSQL ? "= ANY(@seriesIds)" : "IN @seriesIds";
            using var operationLock = MediaFileOperationLock.Acquire(seriesIds);
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            connection.Execute($"DELETE FROM \"EpisodeTrackFiles\" WHERE \"TrackId\" IN (SELECT \"Id\" FROM \"SeriesQualityTracks\" WHERE \"SeriesId\" {condition})", new { seriesIds = seriesIds.ToArray() }, transaction);
            connection.Execute($"DELETE FROM \"SeriesQualityTracks\" WHERE \"SeriesId\" {condition}", new { seriesIds = seriesIds.ToArray() }, transaction);
            connection.Execute($"DELETE FROM \"Series\" WHERE \"Id\" {condition}", new { seriesIds = seriesIds.ToArray() }, transaction);
            transaction.Commit();
        }

        public bool SeriesPathExists(string path)
        {
            return Query(c => c.Path == path).Any();
        }

        public Series FindByTitle(string cleanTitle)
        {
            cleanTitle = cleanTitle.ToLowerInvariant();

            var series = Query(s => s.CleanTitle == cleanTitle)
                                        .ToList();

            return ReturnSingleSeriesOrThrow(series);
        }

        public Series FindByTitle(string cleanTitle, int year)
        {
            cleanTitle = cleanTitle.ToLowerInvariant();

            var series = Query(s => s.CleanTitle == cleanTitle && s.Year == year).ToList();

            return ReturnSingleSeriesOrThrow(series);
        }

        public List<Series> FindByTitleInexact(string cleanTitle)
        {
            var builder = Builder().Where($"instr(@cleanTitle, \"Series\".\"CleanTitle\")", new { cleanTitle = cleanTitle });

            if (_database.DatabaseType == DatabaseType.PostgreSQL)
            {
                builder = Builder().Where($"(strpos(@cleanTitle, \"Series\".\"CleanTitle\") > 0)", new { cleanTitle = cleanTitle });
            }

            return Query(builder).ToList();
        }

        public Series FindByTvdbId(int tvdbId)
        {
            return Query(s => s.TvdbId == tvdbId).SingleOrDefault();
        }

        public Series FindByTvRageId(int tvRageId)
        {
            return Query(s => s.TvRageId == tvRageId).SingleOrDefault();
        }

        public Series FindByImdbId(string imdbId)
        {
            return Query(s => s.ImdbId == imdbId).SingleOrDefault();
        }

        public Series FindByPath(string path)
        {
            return Query(s => s.Path == path)
                        .FirstOrDefault();
        }

        public Dictionary<int, int> AllSeriesTvdbIds()
        {
            using (var conn = _database.OpenConnection())
            {
                var strSql = "SELECT \"Id\" AS Key, \"TvdbId\" AS Value FROM \"Series\"";
                return conn.Query<KeyValuePair<int, int>>(strSql).ToDictionary(x => x.Key, x => x.Value);
            }
        }

        public Dictionary<int, string> AllSeriesPaths()
        {
            using (var conn = _database.OpenConnection())
            {
                var strSql = "SELECT \"Id\" AS Key, \"Path\" AS Value FROM \"Series\"";
                return conn.Query<KeyValuePair<int, string>>(strSql).ToDictionary(x => x.Key, x => x.Value);
            }
        }

        public Dictionary<int, List<int>> AllSeriesTags()
        {
            using (var conn = _database.OpenConnection())
            {
                var strSql = "SELECT \"Id\" AS Key, \"Tags\" AS Value FROM \"Series\" WHERE \"Tags\" IS NOT NULL";
                return conn.Query<KeyValuePair<int, List<int>>>(strSql).ToDictionary(x => x.Key, x => x.Value);
            }
        }

        public Dictionary<int, int> AllSeriesQualityProfiles()
        {
            using (var conn = _database.OpenConnection())
            {
                var strSql = "SELECT \"Id\" AS Key, \"QualityProfileId\" AS Value FROM \"Series\"";
                return conn.Query<KeyValuePair<int, int>>(strSql).ToDictionary(x => x.Key, x => x.Value);
            }
        }

        private Series ReturnSingleSeriesOrThrow(List<Series> series)
        {
            if (series.Count == 0)
            {
                return null;
            }

            if (series.Count == 1)
            {
                return series.First();
            }

            throw new MultipleSeriesFoundException(series, "Expected one series, but found {0}. Matching series: {1}", series.Count, string.Join(", ", series));
        }
    }
}
