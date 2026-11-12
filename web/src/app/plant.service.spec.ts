import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { PlantService } from './plant.service';

describe('PlantService', () => {
  let svc: PlantService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    svc = TestBed.inject(PlantService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('posts shelve requests with user, reason and duration to the versioned API', () => {
    svc.shelve('XMEAS09', 'op1', 'maintenance', 60).subscribe();
    const req = http.expectOne('/api/v1/alarms/XMEAS09/shelve');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ user: 'op1', reason: 'maintenance', durationMinutes: 60 });
    req.flush({});
  });

  it('requests a config diff with from/to query parameters', () => {
    svc.diff(1, 3).subscribe();
    const req = http.expectOne((r) => r.url === '/api/v1/config/diff');
    expect(req.request.params.get('from')).toBe('1');
    expect(req.request.params.get('to')).toBe('3');
    req.flush({ addedTags: [], removedTags: [], changes: [] });
  });

  it('acknowledges by tag and alarm kind', () => {
    svc.acknowledge('XMEAS07', 'HiHi', 'op2').subscribe();
    http.expectOne('/api/v1/alarms/XMEAS07/HiHi/ack').flush({});
  });
});
