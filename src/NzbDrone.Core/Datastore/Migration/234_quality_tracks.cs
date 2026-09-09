using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(234)]
    public class quality_tracks : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Create.TableForModel("SeriesQualityTracks")
                .WithColumn("SeriesId").AsInt32().NotNullable()
                .WithColumn("QualityProfileId").AsInt32().NotNullable()
                .WithColumn("IsPrimary").AsBoolean().NotNullable()
                .WithColumn("Enabled").AsBoolean().NotNullable();

            Create.TableForModel("EpisodeTrackFiles")
                .WithColumn("EpisodeId").AsInt32().NotNullable()
                .WithColumn("TrackId").AsInt32().NotNullable()
                .WithColumn("EpisodeFileId").AsInt32().NotNullable();

            Execute.Sql("CREATE UNIQUE INDEX \"IX_SeriesQualityTracks_Primary\" ON \"SeriesQualityTracks\" (\"SeriesId\") WHERE \"IsPrimary\" = true");
            Execute.Sql("CREATE UNIQUE INDEX \"IX_SeriesQualityTracks_EnabledProfile\" ON \"SeriesQualityTracks\" (\"SeriesId\", \"QualityProfileId\") WHERE \"Enabled\" = true");

            Create.Index().OnTable("SeriesQualityTracks").OnColumn("SeriesId");
            Create.Index().OnTable("EpisodeTrackFiles").OnColumn("EpisodeId").Ascending().OnColumn("TrackId").Ascending().WithOptions().Unique();
            Create.Index().OnTable("EpisodeTrackFiles").OnColumn("TrackId");
            Create.Index().OnTable("EpisodeTrackFiles").OnColumn("EpisodeFileId");

            Execute.Sql("INSERT INTO \"SeriesQualityTracks\" (\"SeriesId\", \"QualityProfileId\", \"IsPrimary\", \"Enabled\") SELECT \"Id\", \"QualityProfileId\", true, true FROM \"Series\"");
            Execute.Sql(@"INSERT INTO ""EpisodeTrackFiles"" (""EpisodeId"", ""TrackId"", ""EpisodeFileId"")
                SELECT e.""Id"", t.""Id"", f.""Id""
                FROM ""Episodes"" e
                JOIN ""SeriesQualityTracks"" t ON t.""SeriesId"" = e.""SeriesId""
                JOIN ""EpisodeFiles"" f ON f.""Id"" = e.""EpisodeFileId"" AND f.""SeriesId"" = e.""SeriesId""");

            // Invalid legacy pointers cannot become ownership claims on another series' file.
            Execute.Sql(@"UPDATE ""Episodes"" SET ""EpisodeFileId"" = 0
                WHERE ""EpisodeFileId"" <> 0 AND NOT EXISTS
                    (SELECT 1 FROM ""EpisodeTrackFiles"" l WHERE l.""EpisodeId"" = ""Episodes"".""Id"")");
        }
    }
}
