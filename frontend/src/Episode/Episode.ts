import ModelBase from 'App/ModelBase';
import { EpisodeFile } from 'EpisodeFile/EpisodeFile';
import Series from 'Series/Series';

export interface EpisodeQualityTrack {
  trackId: number;
  qualityProfileId: number;
  isPrimary: boolean;
  enabled: boolean;
  episodeFileId: number;
  hasFile: boolean;
  qualityCutoffNotMet: boolean;
  cutoffNotMet: boolean;
  customFormatScore: number;
}

interface Episode extends ModelBase {
  seriesId: number;
  tvdbId: number;
  episodeFileId: number;
  seasonNumber: number;
  episodeNumber: number;
  airDate: string;
  airDateUtc?: string;
  lastSearchTime?: string;
  runtime: number;
  absoluteEpisodeNumber?: number;
  sceneSeasonNumber?: number;
  sceneEpisodeNumber?: number;
  sceneAbsoluteEpisodeNumber?: number;
  overview: string;
  title: string;
  episodeFile?: object;
  episodeFiles?: EpisodeFile[];
  qualityTracks?: EpisodeQualityTrack[];
  hasFile: boolean;
  monitored: boolean;
  grabbed?: boolean;
  unverifiedSceneNumbering: boolean;
  series?: Series;
  finaleType?: string;
}

export default Episode;
