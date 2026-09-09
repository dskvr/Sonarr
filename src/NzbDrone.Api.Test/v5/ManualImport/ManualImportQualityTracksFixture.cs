using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles.EpisodeImport.Manual;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Test.Common;
using Sonarr.Api.V5.ManualImport;

namespace NzbDrone.Api.Test.v5.ManualImport
{
    [TestFixture]
    public class ManualImportQualityTracksFixture : TestBase<ManualImportController>
    {
        [TestCase(null)]
        [TestCase(new[] { 10, 20 })]
        public void reprocessing_should_preserve_explicit_target_selection(int[] targets)
        {
            var request = new ManualImportReprocessResource
            {
                Path = "/downloads/episode.mkv",
                SeriesId = 1,
                EpisodeIds = new List<int> { 2 },
                TargetQualityTrackIds = targets == null ? null : new List<int>(targets)
            };
            Mocker.GetMock<IManualImportService>()
                .Setup(s => s.ReprocessItem(request.Path, null, 1, null, request.EpisodeIds, null, null, It.IsAny<List<Language>>(), 0, ReleaseType.Unknown, request.TargetQualityTrackIds, false))
                .Returns(new ManualImportItem { Path = request.Path, TargetQualityTrackIds = request.TargetQualityTrackIds });

            var result = Subject.ReprocessItems(new List<ManualImportReprocessResource> { request });
            var resource = ((Microsoft.AspNetCore.Http.HttpResults.Ok<List<ManualImportResource>>)result.Result).Value[0];

            resource.TargetQualityTrackIds.Should().BeEquivalentTo(request.TargetQualityTrackIds);
            Mocker.GetMock<IManualImportService>().Verify(s => s.ReprocessItem(request.Path, null, 1, null, request.EpisodeIds, null, null, It.IsAny<List<Language>>(), 0, ReleaseType.Unknown, request.TargetQualityTrackIds, false), Times.Once());
        }

        [Test]
        public void explicit_series_reselection_should_forward_target_reset_without_requiring_target_ids()
        {
            var request = new ManualImportReprocessResource
            {
                Path = "/downloads/episode.mkv",
                SeriesId = 1,
                EpisodeIds = new List<int> { 2 },
                ResetQualityTrackTargets = true
            };
            Mocker.GetMock<IManualImportService>()
                .Setup(s => s.ReprocessItem(request.Path, null, 1, null, request.EpisodeIds, null, null, It.IsAny<List<Language>>(), 0, ReleaseType.Unknown, null, true))
                .Returns(new ManualImportItem { Path = request.Path, TargetQualityTrackIds = new List<int> { 10 } });

            var result = Subject.ReprocessItems(new List<ManualImportReprocessResource> { request });

            ((Microsoft.AspNetCore.Http.HttpResults.Ok<List<ManualImportResource>>)result.Result).Value[0].TargetQualityTrackIds.Should().Equal(10);
            Mocker.GetMock<IManualImportService>().Verify(s => s.ReprocessItem(request.Path, null, 1, null, request.EpisodeIds, null, null, It.IsAny<List<Language>>(), 0, ReleaseType.Unknown, null, true), Times.Once());
        }
    }
}
