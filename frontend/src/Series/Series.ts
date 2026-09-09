import ModelBase from 'App/ModelBase';
import ReleaseType from 'InteractiveImport/ReleaseType';
import Language from 'Language/Language';
import Quality from 'Quality/Quality';

export type SeriesType = 'anime' | 'daily' | 'standard';
export type SeriesMonitor =
  | 'all'
  | 'future'
  | 'missing'
  | 'existing'
  | 'recent'
  | 'pilot'
  | 'firstSeason'
  | 'lastSeason'
  | 'monitorSpecials'
  | 'unmonitorSpecials'
  | 'none';

export type SeriesStatus = 'continuing' | 'ended' | 'upcoming' | 'deleted';

export type MonitorNewItems = 'all' | 'none';

export type CoverType = 'poster' | 'banner' | 'fanart' | 'season';

export interface Image {
  coverType: CoverType;
  url: string;
  remoteUrl: string;
}

export interface Statistics {
  qualityTracks?: QualityTrackStatistics[];
  seasonCount: number;
  episodeCount: number;
  episodeFileCount: number;
  percentOfEpisodes: number;
  previousAiring?: Date;
  releaseGroups: string[];
  releaseTypes: ReleaseType[];
  episodeFileQualities: Quality[];
  sizeOnDisk: number;
  totalEpisodeCount: number;
  monitoredEpisodeCount: number;
}

export interface QualityTrackStatistics {
  trackId: number;
  qualityProfileId: number;
  episodeCount: number;
  episodeFileCount: number;
  cutoffUnmetCount: number;
  missingCount: number;
}

export interface Season {
  monitored: boolean;
  seasonNumber: number;
  statistics: Statistics;
}

export interface Ratings {
  votes: number;
  value: number;
}

export interface AlternateTitle {
  seasonNumber: number;
  sceneSeasonNumber?: number;
  title: string;
  sceneOrigin: 'unknown' | 'unknown:tvdb' | 'mixed' | 'tvdb';
  comment?: string;
}

export interface SeriesAddOptions {
  monitor: SeriesMonitor;
  searchForMissingEpisodes: boolean;
  searchForCutoffUnmetEpisodes: boolean;
}

export interface SeriesQualityTrack {
  id: number;
  qualityProfileId: number;
  isPrimary: boolean;
  enabled: boolean;
  episodeFileCount: number;
}

export type ApplyAdditionalQualityProfiles = 'add' | 'remove' | 'replace';

interface Series extends ModelBase {
  added: string;
  alternateTitles: AlternateTitle[];
  certification: string;
  cleanTitle: string;
  ended: boolean;
  firstAired?: string;
  lastAired?: string;
  genres: string[];
  images: Image[];
  imdbId?: string;
  monitored: boolean;
  monitorNewItems: MonitorNewItems;
  network: string;
  originalCountry: string;
  originalLanguage: Language;
  overview: string;
  path: string;
  previousAiring?: string;
  nextAiring?: string;
  qualityProfileId: number;
  additionalQualityProfileIds?: number[];
  qualityTracks?: SeriesQualityTrack[];
  ratings: Ratings;
  rootFolderPath: string;
  runtime: number;
  seasonFolder: boolean;
  seasons: Season[];
  seriesType: SeriesType;
  sortTitle: string;
  statistics?: Statistics;
  status: SeriesStatus;
  tags: number[];
  title: string;
  titleSlug: string;
  tvdbId: number;
  tvMazeId: number;
  tvRageId: number;
  tmdbId: number;
  useSceneNumbering: boolean;
  year: number;
  addOptions: SeriesAddOptions;
}

export default Series;
