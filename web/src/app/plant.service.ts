import { HttpClient } from '@angular/common/http';
import { Injectable, NgZone, inject, signal } from '@angular/core';
import { Observable } from 'rxjs';
import {
  ActiveAlarm, AlarmEvent, AlarmKind, AlarmLoad, ConfigDiff, ConfigVersion, Oee, ReplayStatus, TagValue, TrendPoint,
} from './models';

export const API = '/api/v1';

@Injectable({ providedIn: 'root' })
export class PlantService {
  private readonly http = inject(HttpClient);
  private readonly zone = inject(NgZone);
  /** Most recent alarm events pushed over Server-Sent Events (newest first). */
  readonly events = signal<AlarmEvent[]>([]);
  readonly connected = signal(false);

  active(): Observable<ActiveAlarm[]> { return this.http.get<ActiveAlarm[]>(`${API}/alarms/active`); }
  load(): Observable<AlarmLoad> { return this.http.get<AlarmLoad>(`${API}/kpi/alarm-load`); }
  oee(): Observable<Oee> { return this.http.get<Oee>(`${API}/kpi/oee`); }
  snapshot(): Observable<{ plantTimeSec: number; values: TagValue[] }> { return this.http.get<{ plantTimeSec: number; values: TagValue[] }>(`${API}/tags/snapshot`); }
  trend(tag: string): Observable<TrendPoint[]> { return this.http.get<TrendPoint[]>(`${API}/tags/${encodeURIComponent(tag)}/trend`); }
  replay(): Observable<ReplayStatus> { return this.http.get<ReplayStatus>(`${API}/replay`); }
  restart(run: string): Observable<ReplayStatus> { return this.http.post<ReplayStatus>(`${API}/replay`, { run }); }
  versions(): Observable<{ active: number; versions: ConfigVersion[] }> { return this.http.get<{ active: number; versions: ConfigVersion[] }>(`${API}/config/versions`); }
  diff(from: number, to: number): Observable<ConfigDiff> { return this.http.get<ConfigDiff>(`${API}/config/diff`, { params: { from, to } }); }
  rollback(version: number, author: string, reason: string): Observable<ConfigVersion> {
    return this.http.post<ConfigVersion>(`${API}/config/rollback/${version}`, { author, reason });
  }
  shelve(tag: string, user: string, reason: string, durationMinutes: number): Observable<AlarmEvent> {
    return this.http.post<AlarmEvent>(`${API}/alarms/${encodeURIComponent(tag)}/shelve`, { user, reason, durationMinutes });
  }
  acknowledge(tag: string, kind: AlarmKind, user: string): Observable<AlarmEvent> {
    return this.http.post<AlarmEvent>(`${API}/alarms/${encodeURIComponent(tag)}/${kind}/ack`, { user });
  }

  /** Opens the SSE stream; returns a function that closes it. */
  connect(onEvent?: (e: AlarmEvent) => void): () => void {
    if (typeof EventSource === 'undefined') return () => {};
    const es = new EventSource(`${API}/alarms/stream`);
    es.onopen = () => this.connected.set(true);
    es.onerror = () => this.connected.set(false);
    es.addEventListener('alarm', (m) => {
      const e = JSON.parse((m as MessageEvent).data) as AlarmEvent;
      this.zone.run(() => {
        this.events.update((list) => [e, ...list].slice(0, 50));
        onEvent?.(e);
      });
    });
    return () => es.close();
  }
}
