using System;
using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download.Pending
{
    [TestFixture]
    public class PendingQualityTrackRepositoryFixture : DbTest<PendingReleaseRepository, PendingRelease>
    {
        [Test]
        public void should_roundtrip_target_ids_and_profile_signature_in_existing_metadata_column()
        {
            var release = Subject.Insert(new PendingRelease
            {
                SeriesId = 1,
                Title = "The.Office.S03E01.1080p.HDTV-GROUP",
                Added = DateTime.UtcNow,
                Release = new ReleaseInfo { Guid = "release-guid" },
                ParsedEpisodeInfo = new ParsedEpisodeInfo(),
                AdditionalInfo = new PendingReleaseAdditionalInfo
                {
                    TargetQualityTrackIds = [10, 20],
                    TargetQualityTrackSignatures = new Dictionary<int, string> { [10] = "ten", [20] = "twenty" }
                }
            });

            var restored = Subject.Get(release.Id);

            restored.AdditionalInfo.TargetQualityTrackIds.Should().Equal(10, 20);
            restored.AdditionalInfo.TargetQualityTrackSignatures.Should().BeEquivalentTo(release.AdditionalInfo.TargetQualityTrackSignatures);
            restored.Release.Guid.Should().Be("release-guid");
        }
    }
}
