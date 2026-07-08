# 03 — Steam/Cloud 세이브 위험요소 전수 감사 (리스크 레지스터)

**범위:** `src/STS2Mobile/Steam/*` + `Patches/AppLifecyclePatches.cs` + `Patches/LauncherPatches.cs`(cloud)
**워크트리:** `D:\git\StS2-LMM-fix55` (fix/issue-55-v0108-save-path-compat, 607a83c) — 읽기 전용
**방식:** 코드 정적 감사(폰 미연결, 재현 불가). 데이터 손실 판정은 코드 경로 추적 근거만 — 실측 재현 필요 항목은 "미확정" 표기.
**작성:** steam-integration-specialist, 2026-07-08

---

## 핵심 구조 사실 (판정 근거의 전제)

1. **cloud store 는 앱 수명 싱글턴.** `SteamKit2CloudSaveStore.Instance` 는 ctor 에서 set(`SteamKit2CloudSaveStore.cs:36`), 외부에서 `Dispose()` 호출자 없음(grep 확인). → 스토어와 그 `CloudFileCache` 가 앱 세션 전체 생존.
2. **cloud 파일 캐시는 부팅 1회 열거 후 세션 내 재열거 없음.** `CloudFileCache.EnsureLoaded` 는 `_loaded==true` 면 즉시 리턴(`CloudFileCache.cs:168`). 재열거하는 `Refresh()`(`CloudFileCache.cs:131`)의 호출자는 코드 전체에 **없음**. `WaitForCacheReadyAsync` 도 `_loaded` 면 즉시 true(`CloudFileCache.cs:146-147`) — 재열거 아님.
3. **모든 cloud write 는 단일 펀넬** `SteamKit2CloudSaveStore.WriteFile(string,byte[])` 경유(issue #4 확정). GuardB/GuardC 가 `_cache.Set`·enqueue 이전에 실행(`SteamKit2CloudSaveStore.cs:129,139`).
4. **AutoSync 는 내용 비교 위주.** 존재 여부만 캐시(`FileExists`)에 의존, 내용은 매번 라이브 다운로드(`ReadFileAsync`→네트워크). → 부팅 시점에 **이미 캐시에 존재하던** 파일의 비교는 stale 캐시 영향이 작다. 위험은 "부팅 후 PC 가 새로 만든 파일"에 집중.
5. **`UserDataPathProvider.IsRunningModded` 는 GAME 소유 프로세스 전역 static** 을 런처가 flip(주석 `CloudSyncDecisions.cs:127` "process-wide"). 저장/복원 try-finally 는 있으나 경로 간 상호 락 없음.
6. **write queue 는 인메모리** `BlockingCollection`(`CloudWriteQueue.cs:12`) — 영속화 없음.

---

## 리스크 레지스터

| ID | 위험 | 트리거 시나리오 | 현재 방어 | 잔여 노출 | 심각도 | 권고(방향) | 근거 file:line |
|----|------|----------------|-----------|-----------|--------|------------|----------------|
| **A1** | **세션 내내 stale cloud 캐시** — 부팅 1회 열거 후 세션 전체 그 스냅샷 사용 | 모바일 세션 유지 중 PC 가 클라우드에 push → 모바일 캐시는 부팅 시점 메타데이터. 부팅 후 PC 가 **새로 생성**한 파일은 모바일 캐시상 "부재" | 내용 비교는 라이브 read(부팅 때 존재하던 progress.save 는 안전). Guard·Unverified·Conflict 다이얼로그 | 부팅 뒤 PC 가 만든 current_run/history 는 캐시 "부재" → `AutoSyncFileAsync` LocalOnly→Push 로 **로컬(구/기본)으로 신규 클라우드 덮어씀**; Determine 은 MobileOnly 로 오분류(다이얼로그는 뜨나 stale 정보로 사용자 오판). 세션이 길수록 확대 | 높음 (디싱크; 엣지 치명) | Save Manager 열 때·핸드셰이크 직전에 `cache.Refresh()`(재열거) 강제, 또는 TTL 기반 재열거. 최소한 파괴 판정 직전엔 라이브 존재 재확인 | `CloudFileCache.cs:131,146,168`; `CloudSyncCoordinator.cs:72-73,142-146`(Push branch) |
| **A2** | 낙관적 `_cache.Set` — 업로드 확정 전 캐시를 신규 크기로 갱신 | 업로드가 조용히 실패(F2/F3/G1)해도 캐시는 "업로드됨(size=N @ now)"으로 표기 | Save Manager verify 는 라이브 read 라 잡아냄 | AutoSync 의 `FileExists`/`GetFileSize` 는 캐시 신뢰 → "이미 최신" 오판 | 중 | commit 성공(`file_committed==true`) 후에만 `_cache.Set`, 실패 시 stale 유지/재큐 | `SteamKit2CloudSaveStore.cs:148`; commit `:510` |
| **B1** | mirror-delete-**local** 이 stale 캐시로 오발 | ManualPull/ApplyOne(KeepCloud): 캐시가 current_run "부재"(PC 가 방금 새 run 시작해 stale) → 로컬 current_run**+.backup** 삭제 | ephemeral(`current_run(.save/_mp.save)`)만 대상, progress/history 제외; 삭제 전 `_conflict/discarded` 백업; `.backup` 도 함께 삭제해 좀비 방지 | 진행 중이던 로컬 run 이 사라짐(클라우드/백업에서 복구 가능하나 사용자가 하던 그 run) | 중~높음 | mirror-delete-local 을 캐시가 아닌 **라이브 존재 재확인**(재열거 또는 download 프로브) 뒤에만 수행 | `CloudSyncCoordinator.cs:220-233,388-406`; `LauncherPatches.cs:953-962` |
| **B2** | DeleteFile 이 배치를 우회 | ManualPush 배치 수집 창(`_collectingBatch=true`) 중 `DeleteFile` 는 즉시 enqueue+실행 → write 는 배치 대기, delete 는 선실행 | 삭제 대상 ephemeral 한정 | delete 가 배치 write 보다 먼저 커밋 → 원자적 아님(중단 시 delete 됐는데 write 미완) | 중 | delete 도 배치 컨텍스트에 넣거나 배치 완료 후로 순서화 | `SteamKit2CloudSaveStore.cs:186`(즉시 enqueue) vs `:150-158`; `CloudSyncCoordinator.cs:178` |
| **B3** | 백업 FIFO prune 가 로컬시각 폴더명 정렬 | auto 백업 10세트 초과 시 이름 오름차순 최古 삭제. 폴더명 `yyyyMMdd_HHmmss` **로컬시각**(`MakeTimestamp`) | 수동 백업은 prune 안 함(영속) | DST fallback/시계 되돌림 → 더 최신 세트가 "옛것"으로 정렬되어 최신 백업이 먼저 삭제될 수 있음 | 중 (백업 소실, 라이브 세이브 아님) | 폴더명에 UTC 또는 단조 카운터 포함 | `LocalBackupService.cs:365-398,422-423` |
| **C1** | **ManualPushAllAsync half-open 배치** — flush 없이 즉시 리턴 | 사용자가 Push → UI "Push 완료" 즉시 표시(실제 업로드는 백그라운드 큐). 대량(100+)이면 배경 flush 5s 안에 미완; 중단 시 개별 파일 커밋됨 + `CompleteAppUploadBatch` 미호출 | quit-flush 300s, background-flush 5s | UI 거짓 "완료"; 중단 시 **일부 프로필만 갱신된 불일치 클라우드** → PC 디싱크 반복(리드 지목, 미수정) | 높음 (디싱크 반복) | `ManualPushAllAsync` 끝에서 `cloudStore.Flush()` **await**(이미 Task.Run 백그라운드라 ANR 무관) → "완료"를 실제 flush 후로; B2 와 함께 delete 도 배치화 | `CloudSyncCoordinator.cs:157-201`(200 즉시 리턴); 호출부 `LauncherController.cs:930-940` |
| **C2** | 인메모리 큐 하드킬 유실 | OOM/크래시/스와이프(백그라운드 이벤트 없이) → 큐 잔여 업로드 유실 | 정상 background/quit 는 flush; 로컬은 영속 | 유실된 게 history *.run 이면 게임이 완료 run 을 재기록하지 않아 **다음 세션에도 자동 재업로드 안 됨**(수동 Push 전까지 클라우드에 그 run 없음). 로컬 안전 | 중~높음 (클라우드 run 결손, 로컬 무손실) | 큐 영속화 또는 핸드셰이크에서 history 재조정(로컬↔클라우드 diff 재push) | `CloudWriteQueue.cs:12,67-85`; 재동기 한계 `CloudSyncDecisions.cs:391-403`(progress/current_run 만 검사) |
| **C3/C4** | 업로드 실패의 조용한 스왈로 + 배치 "완료" 오표기 | 개별 `UploadWithRetry` 가 non-TooManyPending 예외를 generic catch → log 후 리턴; 그래도 `CompleteAppUploadBatch` 는 OK 로 호출 | 3회 재시도(TooManyPending 한정) | 사용자에게 실패 미통지; 일부 파일 누락된 배치가 성공(OK)으로 커밋 | 중 | 파일별 성공 집계 → 실패 있으면 UI 통지 + batch_eresult 반영/미완료 처리 | `SteamKit2CloudSaveStore.cs:386-392`(스왈로), `:335-359`(무조건 Complete) |
| **D1** | **`IsRunningModded` 전역 토글 wrong-slot** | 다수 런처 경로가 GAME 전역 static 을 flip(save/restore, 상호 락 없음). 두 cloud 작업 동시(예: Save Manager Task.Run) 또는 GAME 세이브 스레드가 창 중 read → **잘못된 프로필 디렉터리로 read/write** | 핸드셰이크는 게임 시작 전(창 축소); 각 경로 try-finally 복원 | Save Manager(백그라운드 Task.Run)+타 cloud 흐름 겹침, quit-flush↔인세션 업데이트 재시작 등에서 토글 오염 → 다른 슬롯 오염 | 높음 (오슬롯 쓰기=디싱크/손실) | `SavePathCompat` 를 ambient 대신 **명시 `(profile, modded)` 인자**로 리팩터; 과도기엔 토글 창 전체 글로벌 락 | `CloudSyncCoordinator.cs:324-343`; `CloudSyncDecisions.cs:142-147,304-311`; `LauncherPatches.cs:724-733,819-851,868-907` |
| **D2** | 전역 `_collectingBatch` 로 게임 write 혼입 | ManualPush 배치 창 중 게임 AutoSync write 발생 시 그 write 가 `_batchPendingFiles` 로 흡수 | 현재 phase 분리(Push=런처, 게임 미가동)로 창 닫힘 | 향후 인게임에서 Push/Save Manager 허용 시 게임 write 가 수동 배치에 섞임 | 중 (잠재) | 배치 컨텍스트를 전역 플래그 대신 호출 스코프로 전달 | `SteamKit2CloudSaveStore.cs:26,150-158,282-303` |
| **E1** | timestamp 도메인 혼재 | 업로드 timestamp=기기 시계(`UtcNow`), 캐시 cloud timestamp=Steam 서버 시각. `LocalIsMoreRecent`("최근" 강조)가 두 도메인 비교 | 동기 방향은 timestamp 아닌 **내용** 기반이라 직접 파괴 아님 | 기기 시계 오차 → "최근" 배지가 틀린 쪽 지시 → 사용자가 KeepX 오선택 유도 | 중 | 양측 서버시각 사용 또는 배지에 "시각 신뢰불가" 주의; 최종은 내용비교 유지 | 업로드 `SteamKit2CloudSaveStore.cs:145,414-416`; 배지 `CloudSyncDecisions.cs:82-103` |
| **E2** | Comparer Equal→"cloud wins" 로 로컬 변경 소실 | progress.save 가 floors/games/discovered/playtime 4지표 불변인 변경(일부 unlock 플래그·설정)만 다르면 `CompareProgress`=Equal → AutoSync Equal 분기 "cloud wins" | 4지표 cascade + current_run 은 byte-length 타이브레이크 | 지표 미변동 모바일 변경이 클라우드로 덮여 소실; current_run 은 "더 길면 승" → 실제 최신이 짧으면 패배 | 중 (좁음) | Equal 시 전체 content 해시 폴백 또는 필드 병합, 혹은 무성 cloud-wins 대신 프롬프트 | `SaveProgressComparer.cs:40-72,74-98`; 소비 `CloudSyncCoordinator.cs:122-135` |
| **F1** | refresh token 무갱신 | 세션이 `AccessToken=_refreshToken` 재사용, 토큰 갱신 콜백 구독 없음. 만료/철회 시 LoggedOn 실패 | 만료 시 캐시 미로드→local-only(보수적), GuardB cache-not-loaded 차단 | 조용한 클라우드 중단(모든 RPC throw, 업로드 무성 드롭). "세션 만료, 재로그인" UX 없음 → 사용자 미인지 중 디싱크 누적 | 중 | 토큰 갱신 콜백 구독·재저장, auth-expired 를 UI 로 표면화 | `SteamConnection.cs:105`; 저장소 `SteamCredentialStore.cs:63-100`(재저장 트리거 없음) |
| **F2** | idle-race 가 **write** 에는 미재시도 | 30s idle 타이머가 send 직전 소켓 teardown. read 는 `ReadCloudFileWithRetryAsync` 로 재시도하나 write `UploadWithRetry` 는 TooManyPending 만 재시도 | read 경로 재시도 있음 | idle-teardown 취소가 업로드 중 발생 → generic catch → **재시도 없이 드롭**(read/write 비대칭) | 중 | `UploadWithRetry` 에 transient-cancellation 재시도 추가 또는 업로드 구간 `SuspendIdleTimeout` | write `SteamKit2CloudSaveStore.cs:377-392`; read 대비 `CloudSyncDecisions.cs:646-674`; 타이머 `SteamConnection.cs:312-332` |
| **F3** | half-upload 후 commit 실패를 성공 처리 | 블록 PUT(별도 스토리지 호스트) 성공(`uploadSucceeded=true`) 뒤 CM 드롭으로 `ClientCommitFileUpload` throw → finally catch, 재throw 안 함 → Steam 은 미커밋 업로드 폐기 | commit 실패 로그; 로컬 영속 | 코드가 성공으로 간주 + `_cache.Set` 유지 → 파일 실제 미반영(A2 와 결합). AutoSync 는 미검출 | 중~높음 | commit 실패를 업로드 실패로 취급(재시도); `file_committed==true` 일 때만 캐시 확정 | `SteamKit2CloudSaveStore.cs:488-520`(510,519) |
| **G1** | Steam Cloud quota 초과 무성 실패 | 파일수/용량 초과 시 `ClientBeginFileUpload` non-OK → `SendCloud` throw → `UploadWithRetry` generic catch → log only. modded+unmodded 이중 트리 + 프로필당 최대 100 history 로 파일수 팽창 | HistoryFileLimit=100/프로필 | 사용자는 "push 됨"으로 알지만 클라우드 거부(무성) | 중~높음 | quota EResult 명시 감지→UI 통지; modded/unmodded 중복 업로드 재검토 | 업로드 실패 `SteamKit2CloudSaveStore.cs:386-392`; 열거 상한 `CloudSyncCoordinator.cs:19,360` |
| **H1** | canonical 경로 case-fold 없음 | `CanonicalizePath` 는 `user://` 제거+`\`→`/` 만, **소문자화 안 함**. 캐시 키는 Steam 열거 원본 대소문자, 로컬 경로는 게임 헬퍼 | ephemeral 판정/GuardC 는 ToLowerInvariant 사용(별개) | 게임 경로와 Steam 저장 대소문자 불일치 시 `FileExists`=false → 유령/중복 업로드. 미관측·잠재 | 중 | canonical case 정책 확정(대소문자 무시 캐시 키 또는 정규화) | `CloudFileCache.cs:28-31,39-43`(ordinal dict); `CloudSyncCoordinator.cs:381` |
| **H2** | modded/unmodded 논리슬롯의 물리 aliasing | 런처가 3프로필×2모드=6 논리슬롯 열거. 게임 UnifiedSavePath/GetProfileDir 가 통합해 unmodded·modded 가 **같은 물리 파일**을 가리키면 동일 파일 이중 계수(유령 슬롯) + 한 논리슬롯 apply 가 다른 쪽을 무성 변경 | 슬롯별 scope apply(다른 5슬롯 미접근 가정) | 실측서 정체불명 unmodded profile2(1550B, 로컬만) 관측. alias 실재 시 이중 업로드/유령슬롯/교차 덮어쓰기 | 높음 (alias 실재 시) | **미확정** — 게임 decomp 로 public 빌드에서 `IsRunningModded` 가 물리 디렉터리를 실제로 바꾸는지 확인(sts2-game-specialist, task #17). alias 면 resolved 물리경로로 논리슬롯 dedupe | 런처 열거 `CloudSyncCoordinator.cs:318-344`; 런처엔 GetProfileDir/UnifiedSavePath 패치 **없음**(grep) → GAME 측 동작 |

---

## 이미 조치된 항목의 잔여(재발견 아님, 노출만)

- **읽기 순단(idle-race read):** `ReadCloudFileWithRetryAsync` 재시도 + `Unverified`(파괴선택 차단). 잔여 최소. `CloudSyncDecisions.cs:646-681`.
- **GuardB/GuardC:** 빈/손상 write 차단 — 단 **fail-open**(가드 자체가 throw 하면 write 허용, `CloudWriteGuard.cs:118-124,183-189`). 예상 못한 입력이 파서를 비정상 종료시키면 손상 write 통과 가능 → Part A 전체트리 백업이 안전망. 잔여 낮음.
- **issue #31 mirror-delete:** progress/history 제외·ephemeral 한정 게이트는 견고. 잔여는 B1(캐시 stale 로 오발)에 집약.
- **issue #4 단일 펀넬:** 유지됨(모든 write 가 WriteFile 경유). ApplyOne/ManualPush/AutoSync 모두 게이트 통과 확인.

---

## 상위 5 위험 (요약)

1. **A1 — 세션 내내 stale cloud 캐시(재열거 없음).** 스토어 싱글턴+`Refresh()` 무호출로 캐시가 부팅 스냅샷 그대로 세션 전체 사용. 내용 라이브 read 가 progress.save 는 지켜주나, 부팅 후 PC 가 새로 만든 current_run/history 는 캐시상 "부재"→로컬이 신규 클라우드를 덮거나 Determine 오분류. 리드 최우선 질문의 답: **예, stale 캐시가 최신 클라우드를 구버전으로 오판할 창이 존재**(신규생성 파일 한정, 기존 progress 는 완화). 권고: Save Manager/핸드셰이크 직전 재열거.

2. **C1 — ManualPushAllAsync half-open 배치.** flush 없이 즉시 리턴→UI 거짓 "완료", 중단 시 일부 프로필만 갱신된 불일치 클라우드 → PC 디싱크 반복. 이미 백그라운드 Task.Run 이라 ANR 무관하므로 **끝에서 Flush() await** 만으로 대부분 해소.

3. **D1 — `IsRunningModded` 프로세스 전역 토글.** GAME static 을 다경로가 락 없이 flip. 동시 cloud 작업/게임 세이브 스레드와 겹치면 **잘못된 프로필로 read/write**. `SavePathCompat` 를 명시 인자 구조로 리팩터 권고.

4. **H2 — modded/unmodded 물리 aliasing 유령 슬롯(미확정).** 6 논리슬롯이 물리적으로 alias 하면 이중 업로드·교차 덮어쓰기. 실측 phantom profile2 와 정합. **게임 decomp 확인 필요**(game-specialist task #17와 공유).

5. **F3+F2 — write 경로 무성 손실.** commit 실패를 성공 처리(F3), idle-race 가 write 엔 미재시도(F2) — read 대비 방어 비대칭. 로컬은 영속이라 치명은 아니나 A2(낙관 캐시)와 결합해 세션 내 "업로드됨" 오표기. `file_committed` 확인 후 캐시 확정 + 업로드 재시도 보강.

**공통 근본원인 2가지:** (i) 캐시가 세션 스냅샷이라 파괴 판정이 낡은 메타데이터 기반(A1·A2·B1·G1 연쇄), (ii) 프로세스 전역 가변 상태(`IsRunningModded`, `_collectingBatch`)로 동시성·오슬롯(D1·D2·H2). 이 둘을 재열거·명시인자로 걷어내면 대부분 파생 위험이 축소됨.

**미확정(실측 필요):** A1 파괴 서브케이스(신규생성 파일 덮어쓰기)와 B1(mirror-delete-local 오발), H2(물리 aliasing) 는 device-test-qa 의 크로스-디바이스 재현으로 확정 요망 — 폰 미연결로 이번 감사는 코드경로 추적까지.
