import { TestBed } from '@angular/core/testing';
import { ConfigHistoryComponent } from './config-history';

describe('ConfigHistoryComponent', () => {
  it('shows the diff and requires a second click to roll back', async () => {
    await TestBed.configureTestingModule({ imports: [ConfigHistoryComponent] }).compileComponents();
    const f = TestBed.createComponent(ConfigHistoryComponent);
    f.componentRef.setInput('versions', [
      { version: 1, sha256: 'a', author: 'system', message: 'seed', createdUtc: '', rollbackOf: null },
      { version: 2, sha256: 'b', author: 'eng1', message: 'slower TI-9', createdUtc: '', rollbackOf: null },
    ]);
    f.componentRef.setInput('active', 2);
    f.componentRef.setInput('selected', 1);
    f.componentRef.setInput('diff', { addedTags: [], removedTags: [], changes: [{ path: 'tags[XMEAS09].onDelaySamples', before: '3', after: '2' }] });
    f.detectChanges();
    const el: HTMLElement = f.nativeElement;
    expect(el.querySelector('.chg')!.textContent).toContain('tags[XMEAS09].onDelaySamples: 3');
    let rolled: number | undefined;
    f.componentInstance.rollback.subscribe((v) => (rolled = v));
    (el.querySelector('.diff button') as HTMLButtonElement).click();
    f.detectChanges();
    expect(rolled).toBeUndefined();
    (el.querySelector('.diff button.danger') as HTMLButtonElement).click();
    expect(rolled).toBe(1);
  });
});
