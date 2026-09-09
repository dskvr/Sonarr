using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Tv;
using NzbDrone.Core.Tv.Commands;
using Sonarr.Http;

namespace Sonarr.Api.V5.Series;

[V5ApiController("series/editor")]
public class SeriesEditorController : Controller
{
    private readonly ISeriesService _seriesService;
    private readonly IManageCommandQueue _commandQueueManager;
    private readonly SeriesEditorValidator _seriesEditorValidator;
    private readonly ISeriesQualityTrackService _qualityTrackService;
    private readonly IQualityProfileService _qualityProfileService;

    public SeriesEditorController(ISeriesService seriesService, IManageCommandQueue commandQueueManager, SeriesEditorValidator seriesEditorValidator, ISeriesQualityTrackService qualityTrackService, IQualityProfileService qualityProfileService)
    {
        _seriesService = seriesService;
        _commandQueueManager = commandQueueManager;
        _seriesEditorValidator = seriesEditorValidator;
        _qualityTrackService = qualityTrackService;
        _qualityProfileService = qualityProfileService;
    }

    [HttpPut]
    public Results<Ok<List<SeriesResource>>, BadRequest> SaveAll([FromBody] SeriesEditorResource resource)
    {
        var seriesToUpdate = _seriesService.GetSeries(resource.SeriesIds);
        var seriesToMove = new List<BulkMoveSeries>();

        ValidateAdditionalQualityProfiles(resource);

        foreach (var series in seriesToUpdate)
        {
            var additionalProfiles = GetAdditionalQualityProfiles(resource, series);
            _qualityTrackService.ValidateProfiles(series.Id, resource.QualityProfileId ?? series.QualityProfileId, additionalProfiles);
        }

        foreach (var series in seriesToUpdate)
        {
            series.AdditionalQualityProfileIds = GetAdditionalQualityProfiles(resource, series);

            if (resource.Monitored.HasValue)
            {
                series.Monitored = resource.Monitored.Value;
            }

            if (resource.MonitorNewItems.HasValue)
            {
                series.MonitorNewItems = resource.MonitorNewItems.Value;
            }

            if (resource.QualityProfileId.HasValue)
            {
                series.QualityProfileId = resource.QualityProfileId.Value;
            }

            if (resource.SeriesType.HasValue)
            {
                series.SeriesType = resource.SeriesType.Value;
            }

            if (resource.SeasonFolder.HasValue)
            {
                series.SeasonFolder = resource.SeasonFolder.Value;
            }

            if (resource.RootFolderPath.IsNotNullOrWhiteSpace())
            {
                series.RootFolderPath = resource.RootFolderPath;
                seriesToMove.Add(new BulkMoveSeries
                {
                    SeriesId = series.Id,
                    SourcePath = series.Path
                });
            }

            if (resource.Tags != null)
            {
                var newTags = resource.Tags;
                var applyTags = resource.ApplyTags;

                switch (applyTags)
                {
                    case ApplyTags.Add:
                        newTags.ForEach(t => series.Tags.Add(t));
                        break;
                    case ApplyTags.Remove:
                        newTags.ForEach(t => series.Tags.Remove(t));
                        break;
                    case ApplyTags.Replace:
                        series.Tags = new HashSet<int>(newTags);
                        break;
                }
            }

            var validationResult = _seriesEditorValidator.Validate(series);

            if (!validationResult.IsValid)
            {
                throw new ValidationException(validationResult.Errors);
            }

            if (resource.MoveFiles && resource.RootFolderPath.IsNotNullOrWhiteSpace())
            {
                series.RootFolderPath = null;
            }
        }

        var updatedSeries = _seriesService.UpdateSeries(seriesToUpdate, !resource.MoveFiles);

        if (resource.MoveFiles && seriesToMove.Any())
        {
            _commandQueueManager.Push(new BulkMoveSeriesCommand
            {
                DestinationRootFolder = resource.RootFolderPath,
                Series = seriesToMove
            });
        }

        return TypedResults.Ok(updatedSeries.ToResource());
    }

    private void ValidateAdditionalQualityProfiles(SeriesEditorResource resource)
    {
        if (resource.AdditionalQualityProfileIds == null && resource.ApplyAdditionalQualityProfiles == null)
        {
            return;
        }

        if (resource.AdditionalQualityProfileIds == null || resource.ApplyAdditionalQualityProfiles == null ||
            !Enum.IsDefined(resource.ApplyAdditionalQualityProfiles.Value))
        {
            throw new ValidationException(new[] { new ValidationFailure(nameof(resource.ApplyAdditionalQualityProfiles), "AdditionalQualityProfileIds and a valid ApplyAdditionalQualityProfiles operation must be supplied together.") });
        }

        if (resource.AdditionalQualityProfileIds.Distinct().Count() != resource.AdditionalQualityProfileIds.Count)
        {
            throw new ValidationException(new[] { new ValidationFailure(nameof(resource.AdditionalQualityProfileIds), "AdditionalQualityProfileIds must not contain duplicate profiles.") });
        }

        if (resource.AdditionalQualityProfileIds.Any(id => id <= 0 || !_qualityProfileService.Exists(id)))
        {
            throw new ValidationException(new[] { new ValidationFailure(nameof(resource.AdditionalQualityProfileIds), "One or more quality profiles do not exist.") });
        }
    }

    private static List<int>? GetAdditionalQualityProfiles(SeriesEditorResource resource, NzbDrone.Core.Tv.Series series)
    {
        if (resource.AdditionalQualityProfileIds == null)
        {
            return null;
        }

        var existingProfiles = series.QualityTracks.Value.Where(t => t.Enabled && !t.IsPrimary).Select(t => t.QualityProfileId);

        return resource.ApplyAdditionalQualityProfiles switch
        {
            ApplyAdditionalQualityProfiles.Add => existingProfiles.Union(resource.AdditionalQualityProfileIds).ToList(),
            ApplyAdditionalQualityProfiles.Remove => existingProfiles.Except(resource.AdditionalQualityProfileIds).ToList(),
            _ => resource.AdditionalQualityProfileIds.ToList()
        };
    }

    [HttpDelete]
    public NoContent DeleteSeries([FromBody] SeriesEditorResource resource)
    {
        _seriesService.DeleteSeries(resource.SeriesIds, resource.DeleteFiles, resource.AddImportListExclusion);

        return TypedResults.NoContent();
    }
}
