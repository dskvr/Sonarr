import React from 'react';
import { useQueueDetailsForSeries } from 'Activity/Queue/Details/QueueDetailsProvider';
import ProgressBar from 'Components/ProgressBar';
import { sizes } from 'Helpers/Props';
import { getQualityTrackTotals } from 'Series/QualityProfiles/qualityTrackState';
import { SeriesStatus } from 'Series/Series';
import { useSingleSeries } from 'Series/useSeries';
import getProgressBarKind from 'Utilities/Series/getProgressBarKind';
import translate from 'Utilities/String/translate';
import styles from './SeriesIndexProgressBar.css';

interface SeriesIndexProgressBarProps {
  seriesId: number;
  seasonNumber?: number;
  monitored: boolean;
  status: SeriesStatus;
  episodeCount: number;
  episodeFileCount: number;
  totalEpisodeCount: number;
  width: number;
  detailedProgressBar: boolean;
  isStandalone: boolean;
}

function SeriesIndexProgressBar(props: SeriesIndexProgressBarProps) {
  const {
    seriesId,
    seasonNumber,
    monitored,
    status,
    episodeCount,
    episodeFileCount,
    totalEpisodeCount,
    width,
    detailedProgressBar,
    isStandalone,
  } = props;

  const queueDetails = useQueueDetailsForSeries(seriesId, seasonNumber);
  const series = useSingleSeries(seriesId);
  const tracks = (
    seasonNumber === undefined
      ? series?.statistics
      : series?.seasons.find((season) => season.seasonNumber === seasonNumber)
          ?.statistics
  )?.qualityTracks;
  const totals =
    tracks && tracks.length > 1 ? getQualityTrackTotals(tracks) : undefined;

  const newDownloads = queueDetails.count - queueDetails.episodesWithFiles;
  let progress = episodeCount ? (episodeFileCount / episodeCount) * 100 : 100;
  let text = newDownloads
    ? `${episodeFileCount} + ${newDownloads} / ${episodeCount}`
    : `${episodeFileCount} / ${episodeCount}`;

  if (totals) {
    progress = totals.desired ? (totals.present / totals.desired) * 100 : 100;
    text = translate('QualityTrackVersionProgress', {
      present: totals.present,
      desired: totals.desired,
    });
  }

  return (
    <ProgressBar
      className={styles.progressBar}
      containerClassName={isStandalone ? undefined : styles.progress}
      progress={progress}
      kind={
        totals?.cutoffUnmet && totals.missing === 0
          ? 'warning'
          : getProgressBarKind(
              status,
              monitored,
              progress,
              queueDetails.count > 0
            )
      }
      size={detailedProgressBar ? sizes.MEDIUM : sizes.SMALL}
      showText={detailedProgressBar}
      text={text}
      title={`${translate('SeriesProgressBarText', {
        episodeFileCount,
        episodeCount,
        totalEpisodeCount,
        downloadingCount: queueDetails.count,
      })}${
        totals
          ? `\n${translate('QualityTrackProgressDetail', {
              missing: totals.missing,
              cutoffUnmet: totals.cutoffUnmet,
            })}`
          : ''
      }`}
      width={width}
    />
  );
}

export default SeriesIndexProgressBar;
