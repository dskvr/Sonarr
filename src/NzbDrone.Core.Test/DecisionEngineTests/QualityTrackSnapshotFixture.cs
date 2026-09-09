using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.DecisionEngineTests
{
    [TestFixture]
    public class QualityTrackSnapshotFixture
    {
        [Test]
        public void should_fingerprint_quality_and_custom_format_acceptance_rules()
        {
            var specification = new ReleaseTitleSpecification { Value = "original" };
            var profile = new QualityProfile
            {
                Items = Qualities.QualityFixture.GetDefaultQualities(),
                FormatItems = [new ProfileFormatItem { Format = new CustomFormat("Display", specification) { Id = 1 }, Score = 10 }]
            };
            var original = QualityTrackSnapshot.ProfileSignature(profile);
            specification.Value = "changed";
            QualityTrackSnapshot.ProfileSignature(profile).Should().NotBe(original);
            specification.Value = "original";
            profile.CutoffFormatScore++;
            QualityTrackSnapshot.ProfileSignature(profile).Should().NotBe(original);
        }

        [Test]
        public void should_ignore_cosmetic_profile_and_format_changes()
        {
            var specification = new ReleaseTitleSpecification { Value = "pattern", Name = "Before" };
            var profile = new QualityProfile
            {
                Name = "Before",
                Items = Qualities.QualityFixture.GetDefaultQualities(),
                FormatItems = [new ProfileFormatItem { Format = new CustomFormat("Before", specification) { Id = 1 }, Score = 10 }]
            };
            var original = QualityTrackSnapshot.ProfileSignature(profile);
            profile.Name = "After";
            profile.Items[0].Name = "After";
            specification.Name = "After";
            profile.FormatItems[0].Format.Name = "After";

            QualityTrackSnapshot.ProfileSignature(profile).Should().Be(original);
        }

        [Test]
        public void should_roundtrip_signature_dictionary_with_integer_target_ids()
        {
            var signatures = new Dictionary<int, string> { [10] = "signature-ten", [20] = "signature-twenty" };
            var persisted = new Dictionary<string, string> { ["qualityTrackSignatures"] = STJson.ToJson(signatures) };

            QualityTrackSnapshot.ReadSignatures(persisted).Should().BeEquivalentTo(signatures);
            QualityTrackSnapshot.ReadSignatures(null).Should().BeNull();
            QualityTrackSnapshot.ReadSignatures(new Dictionary<string, string>()).Should().BeNull();
        }

        [TestCase("invalid")]
        [TestCase("[]")]
        [TestCase("null")]
        [TestCase(null)]
        public void should_preserve_unverifiable_signature_as_empty_metadata(string value)
        {
            QualityTrackSnapshot.ReadSignatures(new Dictionary<string, string> { ["qualityTrackSignatures"] = value }).Should().BeEmpty();
        }

        [Test]
        public void should_not_recapture_saved_signature_after_profile_edit()
        {
            var profile = new QualityProfile();
            var remote = new RemoteEpisode
            {
                Series = new Series { QualityProfile = profile },
                TargetQualityTrackIds = [10],
                TargetQualityTrackSignatures = new Dictionary<int, string> { [10] = "original-signature" }
            };
            profile.MinFormatScore = 100;

            QualityTrackSnapshot.CaptureSignatures(remote);

            remote.TargetQualityTrackSignatures[10].Should().Be("original-signature");
        }

        [Test]
        public void should_use_track_file_and_profile_without_mutating_shared_episode()
        {
            var profile = new QualityProfile { Id = 2, FormatItems = [] };
            var track = new SeriesQualityTrack { Id = 20, QualityProfileId = 2, QualityProfile = profile };
            var series = new Series { Id = 1, QualityProfileId = 1 };
            var episode = new Episode { Id = 3, SeriesId = 1, EpisodeFileId = 4, Series = series };
            var file = new EpisodeFile { Id = 5 };
            var remote = new RemoteEpisode { Series = series, Episodes = [episode] };

            var result = QualityTrackSnapshot.Create(
                remote,
                track,
                [new EpisodeTrackFile { EpisodeId = 3, TrackId = 20, EpisodeFileId = 5, EpisodeFile = file }]);

            result.Should().NotBeSameAs(remote);
            result.Series.Should().NotBeSameAs(series);
            result.Series.QualityProfile.Value.Should().BeSameAs(profile);
            result.Series.QualityProfileId.Should().Be(2);
            result.Episodes[0].Should().NotBeSameAs(episode);
            result.Episodes[0].EpisodeFileId.Should().Be(5);
            result.Episodes[0].EpisodeFile.Value.Should().BeSameAs(file);
            result.Episodes[0].Series.Should().BeSameAs(result.Series);
            result.TargetQualityTrackIds.Should().Equal(20);
            series.QualityProfileId.Should().Be(1);
            episode.EpisodeFileId.Should().Be(4);
            remote.TargetQualityTrackIds.Should().BeNull();
        }

        [Test]
        public void should_not_use_compatibility_file_when_track_is_missing()
        {
            var episode = new Episode { Id = 3, EpisodeFileId = 4 };
            var result = QualityTrackSnapshot.CreateEpisodes(
                [episode],
                new Series(),
                20,
                [new EpisodeTrackFile { EpisodeId = 3, TrackId = 10, EpisodeFileId = 4 }]);

            result[0].HasFile.Should().BeFalse();
            result[0].EpisodeFile.Should().BeNull();
            episode.HasFile.Should().BeTrue();
        }

        [Test]
        public void should_preserve_single_profile_missing_semantics_without_tracks()
        {
            QualityTrackSnapshot.IsMissing(new Episode { EpisodeFileId = 4 }).Should().BeFalse();
            QualityTrackSnapshot.IsMissing(new Episode()).Should().BeTrue();
        }

        [Test]
        public void should_ignore_disabled_tracks_but_include_missing_enabled_track()
        {
            var series = new Series
            {
                QualityTracks = new List<SeriesQualityTrack>
                {
                    new() { Id = 10, Enabled = true, IsPrimary = true },
                    new() { Id = 20, Enabled = false }
                }
            };
            var episode = new Episode
            {
                EpisodeFileId = 4,
                TrackFiles = new List<EpisodeTrackFile> { new() { TrackId = 10, EpisodeFileId = 4 } }
            };

            QualityTrackSnapshot.IsMissing(episode, series).Should().BeFalse();
            series.QualityTracks.Value[1].Enabled = true;
            QualityTrackSnapshot.IsMissing(episode, series).Should().BeTrue();
        }

        [TestCase("[10,20,10]", new[] { 10, 20 })]
        [TestCase("[]", new int[0])]
        [TestCase("null", new int[0])]
        [TestCase("[0]", new int[0])]
        [TestCase("[-1]", new int[0])]
        [TestCase("not-json", new int[0])]
        [TestCase("[\"10\"]", new int[0])]
        public void should_read_saved_targets_without_reclassifying_invalid_data(string value, int[] expected)
        {
            QualityTrackSnapshot.ReadTargets(new Dictionary<string, string> { ["qualityTrackIds"] = value })
                .Should().Equal(expected);
        }

        [Test]
        public void should_distinguish_legacy_from_empty_saved_intent()
        {
            QualityTrackSnapshot.ReadTargets(null).Should().BeNull();
            QualityTrackSnapshot.ReadTargets(new Dictionary<string, string>()).Should().BeNull();
        }

        [Test]
        public void should_scope_legacy_download_to_original_primary()
        {
            var series = new Series
            {
                QualityTracks = new List<SeriesQualityTrack>
                {
                    new() { Id = 10, Enabled = true, IsPrimary = true },
                    new() { Id = 20, Enabled = true }
                }
            };
            var legacy = new RemoteEpisode { Series = series };

            QualityTrackSnapshot.TargetsOverlap(legacy, new RemoteEpisode { Series = series, TargetQualityTrackIds = [10] }).Should().BeTrue();
            QualityTrackSnapshot.TargetsOverlap(legacy, new RemoteEpisode { Series = series, TargetQualityTrackIds = [20] }).Should().BeFalse();
            QualityTrackSnapshot.TargetsOverlap(legacy, new RemoteEpisode { Series = series, TargetQualityTrackIds = [] }).Should().BeFalse();
        }
    }
}
