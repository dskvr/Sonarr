using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FizzWare.NBuilder;
using FluentAssertions;
using FluentValidation;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.TvTests
{
    [TestFixture]
    public class SeriesQualityTrackRepositoryFixture : DbTest<SeriesRepository, Series>
    {
        private List<QualityProfile> _profiles;

        [SetUp]
        public void Setup()
        {
            _profiles = Builder<QualityProfile>.CreateListOfSize(3).All().With(p => p.Id = 0).With(p => p.Items = new List<QualityProfileQualityItem>()).BuildList();
            Db.InsertMany(_profiles);
        }

        private Series AddSeries(params int[] additionalProfiles)
        {
            var series = Builder<Series>.CreateNew().BuildNew();
            series.TitleSlug = Guid.NewGuid().ToString();
            series.Path = Path.Combine(TempFolder, series.TitleSlug);
            series.TvdbId = Db.All<Series>().Count + 1;
            series.QualityProfileId = _profiles[0].Id;
            series.AdditionalQualityProfileIds = additionalProfiles.ToList();
            return Subject.Insert(series);
        }

        [Test]
        public void should_create_primary_track_without_changing_legacy_profile()
        {
            var series = AddSeries();
            var track = Db.Single<SeriesQualityTrack>();
            track.SeriesId.Should().Be(series.Id);
            track.QualityProfileId.Should().Be(series.QualityProfileId);
            track.IsPrimary.Should().BeTrue();
            track.Enabled.Should().BeTrue();
        }

        [Test]
        public void should_preserve_additional_profiles_on_legacy_update()
        {
            var series = AddSeries(_profiles[1].Id);
            var primaryId = Db.All<SeriesQualityTrack>().Single(t => t.IsPrimary).Id;
            series.AdditionalQualityProfileIds = null;
            series.QualityProfileId = _profiles[2].Id;
            Subject.Update(series);

            var tracks = Db.All<SeriesQualityTrack>();
            tracks.Single(t => t.IsPrimary).Id.Should().Be(primaryId);
            tracks.Single(t => t.IsPrimary).QualityProfileId.Should().Be(_profiles[2].Id);
            tracks.Single(t => !t.IsPrimary).QualityProfileId.Should().Be(_profiles[1].Id);
            tracks.Should().OnlyContain(t => t.Enabled);
        }

        [Test]
        public void should_reactivate_same_track_after_removal()
        {
            var series = AddSeries(_profiles[1].Id);
            var trackId = Db.All<SeriesQualityTrack>().Single(t => !t.IsPrimary).Id;
            series.AdditionalQualityProfileIds.Clear();
            Subject.Update(series);
            Db.All<SeriesQualityTrack>().Single(t => t.Id == trackId).Enabled.Should().BeFalse();

            series.AdditionalQualityProfileIds.Add(_profiles[1].Id);
            Subject.Update(series);
            Db.All<SeriesQualityTrack>().Single(t => t.Id == trackId).Enabled.Should().BeTrue();
            Db.All<SeriesQualityTrack>().Should().HaveCount(2);
        }

        [Test]
        public void should_roll_back_entire_batch_on_conflicting_profile()
        {
            var first = AddSeries(_profiles[1].Id);
            var second = AddSeries(_profiles[1].Id);
            first.AdditionalQualityProfileIds = new List<int> { _profiles[2].Id };
            second.AdditionalQualityProfileIds = null;
            second.QualityProfileId = _profiles[1].Id;

            Assert.Throws<ValidationException>(() => Subject.UpdateMany(new[] { first, second }));
            Db.All<SeriesQualityTrack>().Where(t => t.Enabled && !t.IsPrimary)
                .Should().OnlyContain(t => t.QualityProfileId == _profiles[1].Id);
            Subject.Get(second.Id).QualityProfileId.Should().Be(_profiles[0].Id);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void should_reject_duplicate_or_unknown_additional_profiles(bool duplicate)
        {
            var series = AddSeries();
            series.AdditionalQualityProfileIds = duplicate
                ? new List<int> { _profiles[1].Id, _profiles[1].Id }
                : new List<int> { 999999 };

            Assert.Throws<ValidationException>(() => Subject.Update(series));
            Db.All<SeriesQualityTrack>().Should().ContainSingle();
        }

        [Test]
        public void should_keep_disabled_profile_in_use_only_while_files_are_retained()
        {
            var series = AddSeries(_profiles[1].Id);
            var repository = Mocker.Resolve<SeriesQualityTrackRepository>();
            repository.IsProfileInUse(_profiles[1].Id).Should().BeTrue();
            var track = Db.All<SeriesQualityTrack>().Single(t => !t.IsPrimary);

            series.AdditionalQualityProfileIds.Clear();
            Subject.Update(series);
            repository.IsProfileInUse(_profiles[1].Id).Should().BeFalse();

            Db.Insert(new EpisodeTrackFile { EpisodeId = 1, TrackId = track.Id, EpisodeFileId = 1 });
            repository.IsProfileInUse(_profiles[1].Id).Should().BeTrue();
            repository.IsProfileInUse(99999).Should().BeFalse();
        }

        [Test]
        public void should_return_fresh_tracks_after_each_edit()
        {
            var series = AddSeries();
            series.QualityTracks.Value.Should().ContainSingle();
            series.AdditionalQualityProfileIds.Add(_profiles[1].Id);
            Subject.Update(series);
            series.QualityTracks.Value.Should().HaveCount(2);
            series.QualityTracks.Value.Should().OnlyContain(t => t.QualityProfile.Value != null);
        }

        [Test]
        public void should_remove_owned_links_when_series_is_deleted()
        {
            var series = AddSeries(_profiles[1].Id);
            var track = Db.All<SeriesQualityTrack>().Single(t => !t.IsPrimary);
            Db.Insert(new EpisodeTrackFile { EpisodeId = 1, TrackId = track.Id, EpisodeFileId = 1 });

            Subject.DeleteMany(new[] { series.Id });

            Db.All<Series>().Should().BeEmpty();
            Db.All<SeriesQualityTrack>().Should().BeEmpty();
            Db.All<EpisodeTrackFile>().Should().BeEmpty();
        }

        [Test]
        public void should_reject_unknown_primary_and_leave_existing_tracks_unchanged()
        {
            var series = AddSeries();
            series.QualityProfileId = 999999;
            Assert.Throws<ValidationException>(() => Subject.Update(series));
            Db.Single<SeriesQualityTrack>().QualityProfileId.Should().Be(_profiles[0].Id);
            Subject.Get(series.Id).QualityProfileId.Should().Be(_profiles[0].Id);
        }

        [Test]
        public void should_expose_retained_tracks_without_returning_them_as_acquisition_targets()
        {
            var series = AddSeries(_profiles[1].Id);
            series.AdditionalQualityProfileIds.Clear();
            Subject.Update(series);
            Mocker.SetConstant<ISeriesQualityTrackRepository>(Mocker.Resolve<SeriesQualityTrackRepository>());
            var service = Mocker.Resolve<SeriesQualityTrackService>();

            service.GetTracks(series.Id).Should().HaveCount(2);
            service.GetAllTracks().Should().HaveCount(2);
            var primary = service.GetEnabledTracks(series.Id).Should().ContainSingle().Which;
            service.GetTrack(primary.Id).IsPrimary.Should().BeTrue();
            service.IsProfileInUse(primary.QualityProfileId).Should().BeTrue();
            service.ValidateProfiles(series.Id, primary.QualityProfileId, null);
        }

        [Test]
        public void should_not_create_orphan_tracks_when_updating_unknown_series()
        {
            var series = AddSeries();
            series.Id = 999999;
            Assert.Throws<ModelNotFoundException>(() => Subject.Update(series));
            Db.All<SeriesQualityTrack>().Should().ContainSingle();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_revalidate_profiles_after_concurrent_deletion_before_inserting(bool deleteAdditional)
        {
            var repository = Subject;
            var series = Builder<Series>.CreateNew().BuildNew();
            series.QualityProfileId = _profiles[0].Id;
            series.AdditionalQualityProfileIds = new List<int> { _profiles[1].Id };
            using var started = new ManualResetEventSlim();
            Task<Exception> insertion;

            using (MediaFileOperationLock.AcquireAll())
            {
                insertion = Task.Run(() =>
                {
                    started.Set();
                    try
                    {
                        repository.InsertMany(new[] { series });
                        return null;
                    }
                    catch (Exception exception)
                    {
                        return exception;
                    }
                });

                started.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                insertion.Wait(TimeSpan.FromMilliseconds(100)).Should().BeFalse();
                Db.Delete(_profiles[deleteAdditional ? 1 : 0]);
            }

            insertion.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            insertion.GetAwaiter().GetResult().Should().BeOfType<ValidationException>();
            Db.All<Series>().Should().BeEmpty();
            Db.All<SeriesQualityTrack>().Should().BeEmpty();
        }

        [Test]
        public void should_leave_existing_library_unchanged_for_empty_batches()
        {
            var series = AddSeries(_profiles[1].Id);
            var tracks = Db.All<SeriesQualityTrack>().Select(t => (t.Id, t.SeriesId, t.QualityProfileId)).ToList();
            Subject.InsertMany(Array.Empty<Series>());
            Subject.UpdateMany(Array.Empty<Series>());
            Subject.DeleteMany(Array.Empty<int>());
            Db.All<Series>().Should().ContainSingle().Which.Id.Should().Be(series.Id);
            Db.All<SeriesQualityTrack>().Select(t => (t.Id, t.SeriesId, t.QualityProfileId)).Should().BeEquivalentTo(tracks);
        }

        [Test]
        public void should_reject_invalid_identity_batches_before_writing_valid_members()
        {
            var existing = AddSeries();
            var originalTitle = existing.Title;
            var fresh = Builder<Series>.CreateNew().BuildNew();
            fresh.QualityProfileId = _profiles[0].Id;
            existing.Title = "Changed";
            Assert.Throws<InvalidOperationException>(() => Subject.InsertMany(new[] { fresh, existing }));
            Assert.Throws<InvalidOperationException>(() => Subject.UpdateMany(new[] { existing, fresh }));
            Db.All<Series>().Should().ContainSingle().Which.Title.Should().Be(originalTitle);
            Db.All<SeriesQualityTrack>().Should().ContainSingle();
            fresh.Id.Should().Be(0);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_reject_ancestor_and_descendant_claims_through_parent_aliases(bool ancestor)
        {
            var series = AddSeries();
            var real = Path.Combine(TempFolder, "real-root");
            var alias = Path.Combine(TempFolder, "root-alias");
            Directory.CreateDirectory(real);
            Directory.CreateSymbolicLink(alias, real);
            var owned = ancestor ? Path.Combine(real, "Series", "Nested") : Path.Combine(real, "Series");
            var requested = ancestor ? Path.Combine(alias, "Series") : Path.Combine(alias, "Series", "Nested");
            Subject.UpdatePath(series.Id, owned);
            Subject.HasPathConflict(999, requested).Should().BeTrue();
        }

        [Test]
        public void should_not_treat_unconfigured_legacy_path_as_ownership_of_new_folder()
        {
            var legacy = AddSeries();
            legacy.Path = string.Empty;
            Db.Update(legacy);
            var added = AddSeries(_profiles[1].Id);
            Subject.HasPathConflict(added.Id, added.Path).Should().BeFalse();
            Subject.HasPathConflict(added.Id, null).Should().BeFalse();
            Db.All<Series>().Should().HaveCount(2);
        }

        [Test]
        public void should_release_writer_locks_when_database_disconnects_during_validation()
        {
            var series = AddSeries();
            var database = Mocker.Resolve<IMainDatabase>();
            var connectionCount = 0;
            var failingDatabase = new Mock<IMainDatabase>();
            failingDatabase.SetupGet(d => d.DatabaseType).Returns(database.DatabaseType);
            failingDatabase.Setup(d => d.OpenConnection()).Returns(() =>
            {
                if (++connectionCount == 2)
                {
                    throw new IOException("Connection dropped while validating update");
                }

                return database.OpenConnection();
            });
            var repository = new SeriesRepository(failingDatabase.Object, Mocker.GetMock<IEventAggregator>().Object);

            Assert.Throws<IOException>(() => repository.Update(series));

            var subsequent = Task.Run(() =>
            {
                using var operationLock = MediaFileOperationLock.AcquireAll();
                return true;
            });
            try
            {
                subsequent.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                subsequent.GetAwaiter().GetResult().Should().BeTrue();
            }
            finally
            {
                if (Monitor.IsEntered(MediaFileOperationLock.ForSeries(series.Id)))
                {
                    Monitor.Exit(MediaFileOperationLock.ForSeries(series.Id));
                }
            }

            Subject.Get(series.Id).Path.Should().Be(series.Path);
        }

        [Test]
        public void should_reacquire_global_path_lock_when_path_changes_while_writer_waits()
        {
            var series = AddSeries();
            var requestedPath = series.Path;
            var database = Mocker.Resolve<IMainDatabase>();
            var connectionCount = 0;
            var heldAllBeforeWrite = false;
            using var readCompleted = new ManualResetEventSlim();
            var observedDatabase = new Mock<IMainDatabase>();
            observedDatabase.SetupGet(d => d.DatabaseType).Returns(database.DatabaseType);
            observedDatabase.Setup(d => d.OpenConnection()).Returns(() =>
            {
                var current = Interlocked.Increment(ref connectionCount);
                var connection = database.OpenConnection();
                if (current == 1)
                {
                    connection.Disposed += (_, _) => readCompleted.Set();
                }
                else if (current == 5)
                {
                    heldAllBeforeWrite = Enumerable.Range(0, 64).All(id => Monitor.IsEntered(MediaFileOperationLock.ForSeries(id)));
                }

                return connection;
            });
            var repository = new SeriesRepository(observedDatabase.Object, Mocker.GetMock<IEventAggregator>().Object);
            Task update;
            using (MediaFileOperationLock.AcquireAll())
            {
                update = Task.Run(() => repository.Update(series));
                readCompleted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                Subject.UpdatePath(series.Id, Path.Combine(TempFolder, "new-location"));
            }

            update.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            update.GetAwaiter().GetResult();
            heldAllBeforeWrite.Should().BeTrue();
            Subject.Get(series.Id).Path.Should().Be(requestedPath);
        }
    }
}
