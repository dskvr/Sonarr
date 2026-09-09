using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class EpisodeTrackFileRepositoryFixture : DbTest<EpisodeTrackFileService, EpisodeTrackFile>
    {
        private Series _series;
        private Episode _episode;
        private List<SeriesQualityTrack> _tracks;
        private List<EpisodeFile> _files;

        [SetUp]
        public void Setup()
        {
            Mocker.SetConstant<IEpisodeTrackFileRepository>(Mocker.Resolve<EpisodeTrackFileRepository>());
            _series = Db.Insert(Builder<Series>.CreateNew().BuildNew());
            _episode = Db.Insert(Builder<Episode>.CreateNew().With(e => e.SeriesId = _series.Id).With(e => e.EpisodeFileId = 0).BuildNew());
            _tracks = Builder<SeriesQualityTrack>.CreateListOfSize(2).All().With(t => t.Id = 0).With(t => t.SeriesId = _series.Id)
                .With(t => t.Enabled = true).With(t => t.IsPrimary = false).TheFirst(1).With(t => t.IsPrimary = true).BuildList();
            Db.InsertMany(_tracks);
            _files = Builder<EpisodeFile>.CreateListOfSize(2).All().With(f => f.Id = 0).With(f => f.SeriesId = _series.Id).With(f => f.Quality = new QualityModel(Quality.HDTV720p)).With(f => f.Languages = new List<Language> { Language.English }).BuildList();
            Db.InsertMany(_files);
        }

        private EpisodeTrackFile Link(int track, int file)
        {
            return new EpisodeTrackFile { EpisodeId = _episode.Id, TrackId = _tracks[track].Id, EpisodeFileId = _files[file].Id };
        }

        [Test]
        public void should_share_file_and_preserve_other_track_when_upgrading()
        {
            Subject.ReplaceLinks(_series.Id, new List<EpisodeTrackFile> { Link(0, 0), Link(1, 0) });
            Subject.ReplaceLinks(_series.Id, new List<EpisodeTrackFile> { Link(0, 1) }).Should().ContainSingle().Which.Should().Be(_files[0].Id);

            Subject.IsFileReferenced(_files[0].Id).Should().BeTrue();
            Db.All<EpisodeTrackFile>().Single(l => l.TrackId == _tracks[1].Id).EpisodeFileId.Should().Be(_files[0].Id);
            Db.Single<Episode>().EpisodeFileId.Should().Be(_files[1].Id);
        }

        [Test]
        public void should_project_remaining_file_when_primary_file_is_deleted()
        {
            Subject.ReplaceLinks(_series.Id, new List<EpisodeTrackFile> { Link(0, 0), Link(1, 1) });
            Subject.RemoveFile(_files[0].Id);
            Db.Single<Episode>().EpisodeFileId.Should().Be(_files[1].Id);
            Subject.RemoveFile(_files[1].Id);
            Db.Single<Episode>().EpisodeFileId.Should().Be(0);
        }

        [Test]
        public void should_reject_cross_series_file_without_changing_existing_links()
        {
            Subject.ReplaceLinks(_series.Id, new List<EpisodeTrackFile> { Link(0, 0) });
            _files[1].SeriesId++;
            Db.Update(_files[1]);

            Assert.Throws<ArgumentException>(() => Subject.ReplaceLinks(_series.Id, new List<EpisodeTrackFile> { Link(0, 1) }));
            Db.Single<EpisodeTrackFile>().EpisodeFileId.Should().Be(_files[0].Id);
            Db.Single<Episode>().EpisodeFileId.Should().Be(_files[0].Id);
        }

        [Test]
        public void should_reject_duplicate_targets_without_partial_writes()
        {
            Assert.Throws<ArgumentException>(() => Subject.ReplaceLinks(_series.Id, new List<EpisodeTrackFile> { Link(0, 0), Link(0, 1) }));
            Db.All<EpisodeTrackFile>().Should().BeEmpty();
        }

        [Test]
        public void should_preserve_multi_episode_file_reference()
        {
            var other = Db.Insert(Builder<Episode>.CreateNew().With(e => e.SeriesId = _series.Id).With(e => e.EpisodeFileId = 0).BuildNew());
            var second = Link(0, 0);
            second.EpisodeId = other.Id;
            Subject.ReplaceLinks(_series.Id, new List<EpisodeTrackFile> { Link(0, 0), second });
            Subject.ReplaceLinks(_series.Id, new List<EpisodeTrackFile> { Link(0, 1) });
            Subject.GetForFile(_files[0].Id).Should().ContainSingle().Which.EpisodeId.Should().Be(other.Id);
        }

        [Test]
        public void should_be_idempotent_when_same_import_is_repeated()
        {
            Subject.ReplaceLinks(_series.Id, new List<EpisodeTrackFile> { Link(0, 0) });
            var id = Db.Single<EpisodeTrackFile>().Id;
            Subject.ReplaceLinks(_series.Id, new List<EpisodeTrackFile> { Link(0, 0) }).Should().BeEmpty();
            Db.Single<EpisodeTrackFile>().Id.Should().Be(id);
        }

        [Test]
        public void should_keep_retained_ownership_but_reject_new_import_to_disabled_track()
        {
            Subject.ReplaceLinks(_series.Id, new List<EpisodeTrackFile> { Link(1, 0) });
            _tracks[1].Enabled = false;
            Db.Update(_tracks[1]);
            Subject.ReplaceLinks(_series.Id, new List<EpisodeTrackFile> { Link(1, 0) }).Should().BeEmpty();
            Assert.Throws<ArgumentException>(() => Subject.ReplaceLinks(_series.Id, new List<EpisodeTrackFile> { Link(1, 1) }));
            Db.Single<EpisodeTrackFile>().EpisodeFileId.Should().Be(_files[0].Id);
        }

        [Test]
        public void should_insert_file_and_ownership_together()
        {
            var file = Builder<EpisodeFile>.CreateNew().With(f => f.SeriesId = _series.Id).With(f => f.Quality = new QualityModel(Quality.HDTV720p)).With(f => f.Languages = new List<Language> { Language.English }).BuildNew();
            Subject.ImportFile(file, new List<EpisodeTrackFile> { Link(0, 0), Link(1, 0) });
            file.Id.Should().BeGreaterThan(0);
            Db.All<EpisodeTrackFile>().Should().OnlyContain(l => l.EpisodeFileId == file.Id);
            Db.Single<Episode>().EpisodeFileId.Should().Be(file.Id);
            file.TrackFiles.Value.Should().HaveCount(2);
        }

        [Test]
        public void should_roll_back_file_insert_if_target_is_invalid()
        {
            var file = Builder<EpisodeFile>.CreateNew().With(f => f.SeriesId = _series.Id).With(f => f.Quality = new QualityModel(Quality.HDTV720p)).With(f => f.Languages = new List<Language> { Language.English }).BuildNew();
            var target = Link(0, 0);
            target.EpisodeId = 999999;
            Assert.Throws<ArgumentException>(() => Subject.ImportFile(file, new List<EpisodeTrackFile> { target }));
            file.Id.Should().Be(0);
            Db.All<EpisodeFile>().Should().HaveCount(2);
            Db.All<EpisodeTrackFile>().Should().BeEmpty();
        }

        [Test]
        public void should_remap_file_and_project_both_old_and_new_episodes()
        {
            Subject.ReplaceLinks(_series.Id, new List<EpisodeTrackFile> { Link(0, 0) });
            var other = Db.Insert(Builder<Episode>.CreateNew().With(e => e.SeriesId = _series.Id).With(e => e.EpisodeFileId = 0).BuildNew());
            var target = Link(0, 0);
            target.EpisodeId = other.Id;
            Subject.UpdateFile(_files[0], new List<EpisodeTrackFile> { target }, true);
            Db.All<Episode>().Single(e => e.Id == _episode.Id).EpisodeFileId.Should().Be(0);
            Db.All<Episode>().Single(e => e.Id == other.Id).EpisodeFileId.Should().Be(_files[0].Id);
        }

        [Test]
        public void should_roll_back_remap_and_metadata_if_one_target_is_invalid()
        {
            Subject.ReplaceLinks(_series.Id, new List<EpisodeTrackFile> { Link(0, 0) });
            var previousPath = _files[0].RelativePath;
            _files[0].RelativePath = "renamed.mkv";
            var target = Link(0, 0);
            target.TrackId = 999999;
            Assert.Throws<ArgumentException>(() => Subject.UpdateFile(_files[0], new List<EpisodeTrackFile> { target }, true));
            Db.All<EpisodeFile>().Single(f => f.Id == _files[0].Id).RelativePath.Should().Be(previousPath);
            Db.Single<EpisodeTrackFile>().EpisodeFileId.Should().Be(_files[0].Id);
            Db.Single<Episode>().EpisodeFileId.Should().Be(_files[0].Id);
        }

        [Test]
        public void should_delete_file_with_all_ownership_in_one_transaction()
        {
            Subject.ReplaceLinks(_series.Id, new List<EpisodeTrackFile> { Link(0, 0), Link(1, 0) });
            Subject.DeleteFile(_files[0].Id);
            Db.All<EpisodeFile>().Should().ContainSingle().Which.Id.Should().Be(_files[1].Id);
            Db.All<EpisodeTrackFile>().Should().BeEmpty();
            Db.Single<Episode>().EpisodeFileId.Should().Be(0);
        }

        private void FailProjectionWrites()
        {
            using var connection = Db.OpenConnection();
            if (Db.DatabaseType == DatabaseType.PostgreSQL)
            {
                connection.Execute("CREATE FUNCTION fail_episode_projection() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'Projection write failed'; END; $$ LANGUAGE plpgsql");
                connection.Execute("CREATE TRIGGER fail_episode_projection BEFORE UPDATE OF \"EpisodeFileId\" ON \"Episodes\" FOR EACH ROW EXECUTE FUNCTION fail_episode_projection()");
            }
            else
            {
                connection.Execute("CREATE TRIGGER fail_episode_projection BEFORE UPDATE OF \"EpisodeFileId\" ON \"Episodes\" BEGIN SELECT RAISE(ABORT, 'Projection write failed'); END");
            }
        }

        [Test]
        public void should_restore_links_when_database_rejects_projection_after_replacement()
        {
            Subject.ReplaceLinks(_series.Id, new List<EpisodeTrackFile> { Link(0, 0), Link(1, 0) });
            FailProjectionWrites();

            Assert.Catch(() => Subject.ReplaceLinks(_series.Id, new List<EpisodeTrackFile> { Link(0, 1) }));

            Db.All<EpisodeTrackFile>().Should().OnlyContain(l => l.EpisodeFileId == _files[0].Id);
            Db.Single<Episode>().EpisodeFileId.Should().Be(_files[0].Id);
        }

        [Test]
        public void should_roll_back_inserted_file_when_database_rejects_projection()
        {
            FailProjectionWrites();
            var file = Builder<EpisodeFile>.CreateNew().With(f => f.SeriesId = _series.Id)
                .With(f => f.Quality = new QualityModel(Quality.HDTV720p)).With(f => f.Languages = new List<Language> { Language.English }).BuildNew();

            Assert.Catch(() => Subject.ImportFile(file, new List<EpisodeTrackFile> { Link(0, 0) }));

            file.Id.Should().Be(0);
            Db.All<EpisodeFile>().Should().HaveCount(2);
            Db.All<EpisodeTrackFile>().Should().BeEmpty();
            Db.Single<Episode>().EpisodeFileId.Should().Be(0);
        }

        [Test]
        public void should_count_each_physical_file_once_per_track()
        {
            var other = Db.Insert(Builder<Episode>.CreateNew().With(e => e.SeriesId = _series.Id).With(e => e.EpisodeFileId = 0).BuildNew());
            var second = Link(0, 0);
            second.EpisodeId = other.Id;
            Subject.ReplaceLinks(_series.Id, new List<EpisodeTrackFile> { Link(0, 0), second, Link(1, 0) });

            Subject.GetFileCountsByTrack().Should().BeEquivalentTo(new Dictionary<int, int> { [_tracks[0].Id] = 1, [_tracks[1].Id] = 1 });
            Subject.GetForSeries(_series.Id).Should().HaveCount(3);
            Subject.GetForEpisode(_episode.Id).Should().HaveCount(2);
        }

        [Test]
        public void should_preserve_ownership_during_episode_metadata_updates()
        {
            Subject.ReplaceLinks(_series.Id, new List<EpisodeTrackFile> { Link(0, 0), Link(1, 1) });
            var episode = Db.Single<Episode>();
            episode.Title = "Updated title";
            episode.EpisodeFileId = 0;

            Mocker.Resolve<EpisodeRepository>().Update(episode);

            episode.EpisodeFileId.Should().Be(_files[0].Id);
            episode.TrackFiles.Value.Should().HaveCount(2);
            Db.Single<Episode>().Title.Should().Be("Updated title");
            Subject.GetForEpisode(episode.Id).Should().HaveCount(2);
        }

        [Test]
        public void should_remove_only_deleted_episode_links()
        {
            var other = Db.Insert(Builder<Episode>.CreateNew().With(e => e.SeriesId = _series.Id).With(e => e.EpisodeFileId = 0).BuildNew());
            var second = Link(0, 0);
            second.EpisodeId = other.Id;
            Subject.ReplaceLinks(_series.Id, new List<EpisodeTrackFile> { Link(0, 0), second });

            Mocker.Resolve<EpisodeRepository>().DeleteMany(new[] { _episode.Id });

            Subject.GetForFile(_files[0].Id).Should().ContainSingle().Which.EpisodeId.Should().Be(other.Id);
            Db.All<EpisodeFile>().Should().HaveCount(2);
        }

        [Test]
        public void should_serialize_reference_attachment_with_file_replacement()
        {
            var service = Subject;
            service.ReplaceLinks(_series.Id, new List<EpisodeTrackFile> { Link(0, 0) });
            using var started = new ManualResetEventSlim();
            Task attachment;
            using (MediaFileOperationLock.Acquire(new[] { _series.Id }))
            {
                attachment = Task.Run(() =>
                {
                    started.Set();
                    service.ReplaceLinks(_series.Id, new List<EpisodeTrackFile> { Link(1, 0) });
                });

                started.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                attachment.Wait(TimeSpan.FromMilliseconds(100)).Should().BeFalse();
                service.GetForFile(_files[0].Id).Should().ContainSingle();
            }

            attachment.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            service.GetForFile(_files[0].Id).Should().HaveCount(2);
        }

        [Test]
        public void should_record_legacy_file_update_as_primary_ownership()
        {
            Subject.ReplaceLinks(_series.Id, new List<EpisodeTrackFile> { Link(1, 1) });
            var repository = Mocker.Resolve<EpisodeRepository>();

            repository.SetFileId(_episode, _files[0].Id);

            Db.Single<Episode>().EpisodeFileId.Should().Be(_files[0].Id);
            Subject.GetForEpisode(_episode.Id).Should().HaveCount(2);
            Subject.GetForFile(_files[1].Id).Should().ContainSingle();
            repository.ClearFileId(_episode, true);
            Db.Single<Episode>().EpisodeFileId.Should().Be(_files[1].Id);
            Subject.GetForEpisode(_episode.Id).Should().ContainSingle().Which.TrackId.Should().Be(_tracks[1].Id);
        }
    }
}
