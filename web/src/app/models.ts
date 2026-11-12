export type Priority = 'Low' | 'Medium' | 'High' | 'Critical';
export type AlarmKind = 'HiHi' | 'Hi' | 'Lo' | 'LoLo' | 'Deviation';

export interface ActiveAlarm {
  tag: string;
  kind: AlarmKind;
  priority: Priority;
  raisedAtSec: number;
  value: number;
  limit: number;
  annunciated: boolean;
  suppressedBy: string | null;
  acknowledged: boolean;
  detail: string | null;
}

export interface AlarmEvent {
  timeSec: number;
  tag: string;
  kind: AlarmKind | null;
  type: string;
  priority: Priority;
  annunciated: boolean;
  suppressedBy: string | null;
  detail: string | null;
  user: string | null;
}

export interface AlarmLoad {
  plantTimeSec: number;
  annunciatedLast10Min: number;
  annunciatedPer10MinAverage: number;
  inFlood: boolean;
  activeAlarms: number;
  shelved: number;
}

export interface Oee { availability: number; performance: number; quality: number; oee: number; samples: number; }
export interface TagValue { tag: string; description: string; units: string; unit: string; value: number; }
export interface TrendPoint { t: number; v: number; }
export interface ConfigVersion { version: number; sha256: string; author: string; message: string; createdUtc: string; rollbackOf: number | null; }
export interface FieldChange { path: string; before: string | null; after: string | null; }
export interface ConfigDiff { addedTags: string[]; removedTags: string[]; changes: FieldChange[]; }
export interface ReplayStatus { run: string; cursor: number; length: number; faultOnsetSample: number | null; fault: string | null; configVersion: number; paused: boolean; }
export interface TagAlarmConfig { tag: string; hi?: number; lo?: number; hiHi?: number; loLo?: number; enabled: boolean; }

export const PRIORITY_RANK: Record<Priority, number> = { Critical: 4, High: 3, Medium: 2, Low: 1 };

/** Plant time (seconds since replay start) as HH:MM. */
export function plantClock(sec: number): string {
  const m = Math.floor(sec / 60);
  return `${String(Math.floor(m / 60)).padStart(2, '0')}:${String(m % 60).padStart(2, '0')}`;
}
