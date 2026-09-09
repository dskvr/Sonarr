import { useQueryClient } from '@tanstack/react-query';
import React, { useCallback, useEffect } from 'react';
import Alert from 'Components/Alert';
import Icon from 'Components/Icon';
import Label from 'Components/Label';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import Column from 'Components/Table/Column';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import Episode from 'Episode/Episode';
import useEpisode, { EpisodeEntity } from 'Episode/useEpisode';
import { useEpisodeFile } from 'EpisodeFile/EpisodeFileProvider';
import useEpisodeFiles, {
  useDeleteEpisodeFile,
} from 'EpisodeFile/useEpisodeFiles';
import { icons, kinds, sizes } from 'Helpers/Props';
import EpisodeQualityTrackSummary from 'Series/QualityProfiles/EpisodeQualityTrackSummary';
import { getEpisodeQualityTrackState } from 'Series/QualityProfiles/qualityTrackState';
import Series from 'Series/Series';
import { useSingleSeries } from 'Series/useSeries';
import QualityProfileName from 'Settings/Profiles/Quality/QualityProfileName';
import translate from 'Utilities/String/translate';
import EpisodeAiring from './EpisodeAiring';
import EpisodeFileRow from './EpisodeFileRow';
import EpisodeVersionFileRow from './EpisodeVersionFileRow';
import styles from './EpisodeSummary.css';

const COLUMNS: Column[] = [
  {
    name: 'path',
    label: () => translate('Path'),
    isSortable: false,
    isVisible: true,
  },
  {
    name: 'size',
    label: () => translate('Size'),
    isSortable: false,
    isVisible: true,
  },
  {
    name: 'languages',
    label: () => translate('Languages'),
    isSortable: false,
    isVisible: true,
  },
  {
    name: 'quality',
    label: () => translate('Quality'),
    isSortable: false,
    isVisible: true,
  },
  {
    name: 'customFormats',
    label: () => translate('Formats'),
    isSortable: false,
    isVisible: true,
  },
  {
    name: 'customFormatScore',
    label: React.createElement(Icon, {
      name: icons.SCORE,
      title: () => translate('CustomFormatScore'),
    }),
    isSortable: true,
    isVisible: true,
  },
  {
    name: 'actions',
    label: '',
    isSortable: false,
    isVisible: true,
  },
];

interface EpisodeSummaryProps {
  seriesId: number;
  episodeId: number;
  episodeEntity: EpisodeEntity;
  episodeFileId?: number;
}

function EpisodeSummary({
  seriesId,
  episodeId,
  episodeEntity,
  episodeFileId,
}: EpisodeSummaryProps) {
  const queryClient = useQueryClient();
  const series = useSingleSeries(seriesId) as Series;
  const { qualityProfileId, network } = series;

  const { airDateUtc, overview, qualityTracks, episodeFiles } = useEpisode(
    episodeId,
    episodeEntity
  ) as Episode;

  const {
    path,
    mediaInfo,
    size,
    languages,
    quality,
    qualityCutoffNotMet,
    customFormats,
    customFormatScore,
  } = useEpisodeFile(episodeFileId) ?? {};

  const { deleteEpisodeFile } = useDeleteEpisodeFile(
    episodeFileId!,
    episodeEntity
  );

  const handleDeleteEpisodeFile = useCallback(() => {
    deleteEpisodeFile();
  }, [deleteEpisodeFile]);

  useEffect(() => {
    if (episodeFileId && !path) {
      queryClient.invalidateQueries({ queryKey: ['/episodeFile'] });
    }
  }, [episodeFileId, path, queryClient]);

  const hasOverview = !!overview;
  const trackState = getEpisodeQualityTrackState(qualityTracks);
  const showVersions =
    trackState.isMultiple ||
    qualityTracks?.some((track) => !track.enabled && track.hasFile);
  const fileIds = [
    ...new Set(
      qualityTracks
        ?.filter((track) => track.hasFile)
        .map((track) => track.episodeFileId)
    ),
  ];
  const {
    data: versionFiles,
    isFetching: isFetchingVersions,
    error: versionsError,
  } = useEpisodeFiles({ episodeFileIds: showVersions ? fileIds : [] });

  return (
    <div>
      <div>
        <span className={styles.infoTitle}>{translate('Airs')}</span>

        <EpisodeAiring airDateUtc={airDateUtc} network={network} />
      </div>

      <div>
        <span className={styles.infoTitle}>{translate('QualityProfile')}</span>

        <Label kind={kinds.PRIMARY} size={sizes.MEDIUM}>
          <QualityProfileName qualityProfileId={qualityProfileId} />
        </Label>
      </div>

      <EpisodeQualityTrackSummary
        tracks={qualityTracks}
        files={versionFiles.length ? versionFiles : episodeFiles}
        showFiles={true}
      />

      <div className={styles.overview}>
        {hasOverview ? overview : translate('NoEpisodeOverview')}
      </div>

      {showVersions && isFetchingVersions ? <LoadingIndicator /> : null}
      {showVersions && versionsError ? (
        <Alert kind="danger">{translate('EpisodeFilesLoadError')}</Alert>
      ) : null}

      {showVersions ? (
        <Table columns={COLUMNS}>
          <TableBody>
            {fileIds.map((id) => (
              <EpisodeVersionFileRow
                key={id}
                id={id}
                series={series}
                tracks={qualityTracks ?? []}
                columns={COLUMNS}
                episodeEntity={episodeEntity}
                file={
                  versionFiles.find((file) => file.id === id) ??
                  episodeFiles?.find((file) => file.id === id)
                }
              />
            ))}
          </TableBody>
        </Table>
      ) : null}

      {!showVersions && path ? (
        <Table columns={COLUMNS}>
          <TableBody>
            <EpisodeFileRow
              path={path}
              size={size!}
              languages={languages!}
              quality={quality!}
              qualityCutoffNotMet={qualityCutoffNotMet!}
              customFormats={customFormats!}
              customFormatScore={customFormatScore!}
              mediaInfo={mediaInfo!}
              columns={COLUMNS}
              onDeleteEpisodeFile={handleDeleteEpisodeFile}
            />
          </TableBody>
        </Table>
      ) : null}
    </div>
  );
}

export default EpisodeSummary;
