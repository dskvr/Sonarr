using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Common.Exceptions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Datastore.Migration
{
    [TestFixture]
    public class quality_tracksFixture : MigrationTest<quality_tracks>
    {
        private void GivenLibrary(quality_tracks migration)
        {
            migration.Insert.IntoTable("Series").Row(new { TvdbId = 1, TvRageId = 0, TvMazeId = 0, OriginalLanguage = 1, Status = 0, Images = "[]", Title = "Series", CleanTitle = "series", Path = "/library/series", QualityProfileId = 5, Monitored = true, SeasonFolder = true, Runtime = 45, SeriesType = 0, UseSceneNumbering = false, Added = DateTime.UtcNow, Seasons = "[]", Tags = "[]" });
            migration.Insert.IntoTable("EpisodeFiles").Row(new { SeriesId = 1, SeasonNumber = 1, RelativePath = "series.mkv", Size = 10L, DateAdded = DateTime.UtcNow, Quality = "{}", Languages = "[]", IndexerFlags = 0, ReleaseType = 0 });
            migration.Insert.IntoTable("EpisodeFiles").Row(new { SeriesId = 99, SeasonNumber = 1, RelativePath = "other.mkv", Size = 200L, DateAdded = DateTime.UtcNow, Quality = "{}", Languages = "[]", IndexerFlags = 0, ReleaseType = 0 });
            foreach (var episodeId in new[] { 1, 2, 3, 4, 5 })
            {
                var fileId = episodeId < 3 ? 1 : episodeId == 3 ? 0 : episodeId == 4 ? 2 : 999;
                migration.Insert.IntoTable("Episodes").Row(new { SeriesId = 1, EpisodeFileId = fileId, SeasonNumber = 1, EpisodeNumber = episodeId, Title = "Episode", Monitored = true, UnverifiedSceneNumbering = false, Runtime = 45 });
            }
        }

        [Test]
        public void should_backfill_shared_file_without_changing_existing_ids_or_paths()
        {
            using var connection = WithDapperMigrationTestDb(GivenLibrary);
            var track = connection.QuerySingle<SeriesQualityTrack>("SELECT * FROM \"SeriesQualityTracks\"");
            track.SeriesId.Should().Be(1);
            track.QualityProfileId.Should().Be(5);
            track.IsPrimary.Should().BeTrue();
            track.Enabled.Should().BeTrue();

            var links = connection.Query<EpisodeTrackFile>("SELECT * FROM \"EpisodeTrackFiles\"").ToList();
            links.Select(l => l.EpisodeId).Should().BeEquivalentTo(new[] { 1, 2 });
            links.Should().OnlyContain(l => l.EpisodeFileId == 1 && l.TrackId == track.Id);
            connection.Query<int>("SELECT \"Id\" FROM \"Episodes\"").Should().BeEquivalentTo(new[] { 1, 2, 3, 4, 5 });
            connection.Query<string>("SELECT \"RelativePath\" FROM \"EpisodeFiles\"").Should().BeEquivalentTo(new[] { "series.mkv", "other.mkv" });
            connection.ExecuteScalar<string>("SELECT \"Path\" FROM \"Series\" WHERE \"Id\" = 1").Should().Be("/library/series");
        }

        [Test]
        public void should_clear_invalid_legacy_pointers_without_claiming_other_series_files()
        {
            using var connection = WithDapperMigrationTestDb(GivenLibrary);
            connection.Query<int>("SELECT \"EpisodeFileId\" FROM \"Episodes\" WHERE \"Id\" >= 3").Should().OnlyContain(id => id == 0);
            connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"EpisodeFiles\"").Should().Be(2);
        }

        [Test]
        public void should_migrate_empty_library()
        {
            using var connection = WithDapperMigrationTestDb();
            connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"SeriesQualityTracks\"").Should().Be(0);
            connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"EpisodeTrackFiles\"").Should().Be(0);
        }

        [Test]
        public void should_enforce_primary_and_episode_track_uniqueness()
        {
            using var connection = WithDapperMigrationTestDb(GivenLibrary);
            Assert.That(() => connection.Execute("INSERT INTO \"SeriesQualityTracks\" (\"SeriesId\", \"QualityProfileId\", \"IsPrimary\", \"Enabled\") VALUES (1, 6, true, true)"), Throws.Exception);
            Assert.That(() => connection.Execute("INSERT INTO \"EpisodeTrackFiles\" (\"EpisodeId\", \"TrackId\", \"EpisodeFileId\") SELECT \"EpisodeId\", \"TrackId\", 2 FROM \"EpisodeTrackFiles\""), Throws.Exception);
        }

        [Test]
        public void should_not_repeat_backfill_on_second_startup()
        {
            using (var connection = WithDapperMigrationTestDb(GivenLibrary))
            {
                connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"SeriesQualityTracks\"").Should().Be(1);
            }

            var database = Mocker.Resolve<DbFactory>().Create(new MigrationContext(MigrationType, MigrationVersion));
            using var restarted = database.OpenConnection();
            restarted.ExecuteScalar<int>("SELECT COUNT(*) FROM \"SeriesQualityTracks\"").Should().Be(1);
            restarted.ExecuteScalar<int>("SELECT COUNT(*) FROM \"EpisodeTrackFiles\"").Should().Be(2);
        }

        [Test]
        public void should_roll_back_failed_migration_and_retry_without_losing_legacy_data()
        {
            var legacy = WithTestDb(new MigrationContext(MigrationType, MigrationVersion - 1) { BeforeMigration = _ => { } });
            var series = legacy.Insert(Builder<Series>.CreateNew().With(s => s.QualityProfileId = 5).BuildNew());
            var file = legacy.Insert(Builder<EpisodeFile>.CreateNew().With(f => f.SeriesId = series.Id)
                .With(f => f.Quality = new QualityModel(Quality.HDTV720p)).With(f => f.Languages = new List<Language> { Language.English }).BuildNew());
            var episode = legacy.Insert(Builder<Episode>.CreateNew().With(e => e.SeriesId = series.Id).With(e => e.EpisodeFileId = file.Id).BuildNew());
            var invalid = legacy.Insert(Builder<Episode>.CreateNew().With(e => e.SeriesId = series.Id).With(e => e.EpisodeFileId = 999999).BuildNew());
            using var connection = legacy.OpenConnection();
            if (legacy.DatabaseType == DatabaseType.PostgreSQL)
            {
                connection.Execute("CREATE FUNCTION fail_quality_migration() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'Interrupted migration'; END; $$ LANGUAGE plpgsql");
                connection.Execute("CREATE TRIGGER fail_quality_migration BEFORE UPDATE OF \"EpisodeFileId\" ON \"Episodes\" FOR EACH ROW EXECUTE FUNCTION fail_quality_migration()");
            }
            else
            {
                connection.Execute("CREATE TRIGGER fail_quality_migration BEFORE UPDATE OF \"EpisodeFileId\" ON \"Episodes\" BEGIN SELECT RAISE(ABORT, 'Interrupted migration'); END");
            }

            var factory = Mocker.Resolve<DbFactory>();
            Assert.Throws<SonarrStartupException>(() => factory.Create(new MigrationContext(MigrationType, MigrationVersion)));

            connection.ExecuteScalar<int>("SELECT MAX(\"Version\") FROM \"VersionInfo\"").Should().Be((int)MigrationVersion - 1);
            connection.ExecuteScalar<int>("SELECT \"EpisodeFileId\" FROM \"Episodes\" WHERE \"Id\" = @Id", invalid).Should().Be(999999);
            connection.ExecuteScalar<int>("SELECT \"EpisodeFileId\" FROM \"Episodes\" WHERE \"Id\" = @Id", episode).Should().Be(file.Id);

            if (legacy.DatabaseType == DatabaseType.PostgreSQL)
            {
                connection.ExecuteScalar<int>("SELECT COUNT(*) FROM information_schema.tables WHERE table_name IN ('SeriesQualityTracks', 'EpisodeTrackFiles')").Should().Be(0);
                connection.Execute("DROP TRIGGER fail_quality_migration ON \"Episodes\"");
                connection.Execute("DROP FUNCTION fail_quality_migration()");
            }
            else
            {
                connection.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('SeriesQualityTracks', 'EpisodeTrackFiles')").Should().Be(0);
                connection.Execute("DROP TRIGGER fail_quality_migration");
            }

            factory.Create(new MigrationContext(MigrationType, MigrationVersion));
            connection.Query<SeriesQualityTrack>("SELECT * FROM \"SeriesQualityTracks\"").Should().ContainSingle().Which.SeriesId.Should().Be(series.Id);
            connection.Query<EpisodeTrackFile>("SELECT * FROM \"EpisodeTrackFiles\"").Should().ContainSingle().Which.EpisodeFileId.Should().Be(file.Id);
            connection.ExecuteScalar<string>("SELECT \"RelativePath\" FROM \"EpisodeFiles\" WHERE \"Id\" = @Id", file).Should().Be(file.RelativePath);
        }
    }
}
