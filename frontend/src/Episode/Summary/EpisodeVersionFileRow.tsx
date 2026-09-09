import React, { useCallback } from 'react';
import Column from 'Components/Table/Column';
import { EpisodeQualityTrack } from 'Episode/Episode';
import { EpisodeEntity } from 'Episode/useEpisode';
import useEpisodes from 'Episode/useEpisodes';
import { EpisodeFile } from 'EpisodeFile/EpisodeFile';
import { useEpisodeFile } from 'EpisodeFile/EpisodeFileProvider';
import { useDeleteEpisodeFile } from 'EpisodeFile/useEpisodeFiles';
import QualityTrackNames from 'Series/QualityProfiles/QualityTrackNames';
import Series from 'Series/Series';
import { useQualityProfilesData } from 'Settings/Profiles/Quality/useQualityProfiles';
import translate from 'Utilities/String/translate';
import EpisodeFileRow from './EpisodeFileRow';

interface EpisodeVersionFileRowProps {
  id: number;
  file?: EpisodeFile;
  series: Series;
  tracks: EpisodeQualityTrack[];
  columns: Column[];
  episodeEntity: EpisodeEntity;
}

function EpisodeVersionFileRow({
  id,
  file: suppliedFile,
  series,
  tracks,
  columns,
  episodeEntity,
}: EpisodeVersionFileRowProps) {
  const cachedFile = useEpisodeFile(id);
  const file = suppliedFile ?? cachedFile;
  const profiles = useQualityProfilesData();
  const { data: episodes } = useEpisodes({
    seriesId: series.id,
    seasonNumber: undefined,
    isSelection: true,
  });
  const { deleteEpisodeFile } = useDeleteEpisodeFile(id, episodeEntity);
  const handleDelete = useCallback(
    () => deleteEpisodeFile(),
    [deleteEpisodeFile]
  );

  if (!file) {
    return null;
  }

  const owners =
    series.qualityTracks?.filter((track) =>
      file.qualityTrackIds?.includes(track.id)
    ) ?? [];
  const profileNames = owners
    .map(
      (track) =>
        profiles.find((profile) => profile.id === track.qualityProfileId)
          ?.name ?? track.qualityProfileId
    )
    .join(', ');
  const associatedEpisodes = episodes.filter((episode) =>
    file.episodeIds?.includes(episode.id)
  );
  const episodeNames = associatedEpisodes
    .map(
      (episode) =>
        `${episode.seasonNumber}x${episode.episodeNumber} - ${episode.title}`
    )
    .join(', ');

  return (
    <EpisodeFileRow
      {...file}
      qualityCutoffNotMet={tracks.some(
        (track) =>
          track.enabled && track.episodeFileId === id && track.cutoffNotMet
      )}
      columns={columns}
      isDeleteDisabled={
        !file.episodeIds?.length ||
        associatedEpisodes.length !== file.episodeIds.length ||
        owners.length !== file.qualityTrackIds?.length
      }
      ownership={
        <QualityTrackNames series={series} trackIds={file.qualityTrackIds} />
      }
      deleteMessage={translate('DeleteEpisodeVersionFileMessage', {
        path: file.path,
        profiles: profileNames,
        episodes: episodeNames,
      })}
      onDeleteEpisodeFile={handleDelete}
    />
  );
}

export default EpisodeVersionFileRow;
