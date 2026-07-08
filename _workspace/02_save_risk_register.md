# 세이브/클라우드 종합 리스크 레지스터 (2026-07-08)

사용자 지시("save 관련 모든 위험요소 재탐색 + SteamKit2 best practice 조사")에 대한 종합.
상세 근거: `03_steam_risk_audit.md`(steam-측 18건), `04_game_risk_audit.md`(game-측 G1~G9), `05_steamkit2_research.md`(SteamKit2/Valve 공식 자료).

## 우선순위 통합 (리더 판정)

### P0 — 즉시 수정 가치 (실사고 직결 / 방어 전무)

| # | 위험 | 출처 | 요지 | 권고 |
|---|---|---|---|---|
| P0-1 | **half-open AppUploadBatch → PC 디싱크 반복 fail** | steam C1 + 리서치 Q1 | **Valve 공식 문서로 확정**: CompleteAppUploadBatch 미호출 시 해당 user+app 신규 업로드 수 분 차단("too many pending requests") — 사용자가 겪은 PC 반복 실패와 정확히 일치. ManualPushAll은 Flush 없이 "완료" 거짓 표시. SteamKit2 3.4.0이 최신이라 업그레이드로 해결 불가 | ① CompleteAppUploadBatch를 try/finally 무조건 호출(실패 시 eresult=2) ② Blocking 변형 await ③ 열린 batch_id 영속화→차세션 청소 ④ ManualPushAll 끝에 Flush await(이미 Task.Run이라 ANR 무관) + UI 진행표시 |
| P0-2 | **베타→퍼블릭 버전 다운그레이드 lossy/파국 재저장** | game G1a/G1b | v22 progress를 v21 빌드가 열면 신규 필드 폐기(lossy) 또는 scavenge 실패 시 **CreateDefault()+즉시 SaveProgress** → 빈 진행도가 다음 자동저장에서 클라우드 커리어 덮음. GuardB/C 둘 다 통과(유효 JSON). **사용자 기기에서 이미 lossy 경로 실측**(v22→v21 RecoveredWithDataLoss) | progress push 결정부에 **schema_version 다운그레이드 게이트**: 로컬 schema < 클라우드 schema면 auto-push 차단 + 사용자 다이얼로그 |

### P1 — 저비용 고효과

| # | 위험 | 출처 | 요지 | 권고 |
|---|---|---|---|---|
| P1-1 | 세션 내내 stale cloud 캐시 | steam A1 | CloudFileCache.Refresh() 호출자 전무 — 부팅 1회 스냅샷. 세션 중 PC가 **새로 만든** 파일을 "부재"로 오판(신규 run/history 덮어쓰기·MobileOnly 오분류) | Save Manager 열기/pre-PLAY 핸드셰이크 직전 재열거(Refresh) |
| P1-2 | 인세션 업데이트/AtlasWipe 재시작이 flush 우회 | game G7 | LauncherController:823·:1075 restartApp 직접 호출 → 큐잉 업로드 유실 | 두 지점 restartApp 앞 Flush 추가 (2곳, 순수 런처) |
| P1-3 | settings.save 클라우드 혼입 | game G8 | SaveManager 1-인자 생성이라 기기전용 설정이 클라우드로 감(게임 PC는 2-인자 local-only) | `new SaveManager(wrappedStore, localStore)` 정합화 |
| P1-4 | write 무성 손실 | steam F3+F2 | commit 실패를 성공 처리 + write엔 transient 재시도 없음(read만 있음) | file_committed 확인 후 캐시 확정 + 업로드 취소 재시도 |

### P2 — 구조 개선

| # | 위험 | 출처 | 요지 | 권고 |
|---|---|---|---|---|
| P2-1 | IsRunningModded 프로세스 전역 토글 | steam D1 | 6+ 경로가 락 없이 flip/복원 — UI busy-guard(코드 296)로 런처 내 경합은 차단했으나 게임 스레드와의 이론적 경합 잔존 | SavePathCompat를 명시 (profile, modded) 인자 구조로 리팩터 |
| P2-2 | SHA 미비교 불필요 업로드 | 리서치 Q1 권고 | 내용 동일도 재업로드 → change number 상승 → PC 충돌 유발 가능 | SHA/메타 비교 후 변경분만 업로드 |
| P2-3 | refresh token 수명 관리 | steam F + 리서치 Q4 | 만료 시 UX 없음(실측 ~200일+라 여유) | exp 사전점검 + 롤링 갱신(renewal_type=Allow) |

### 운영 안내 / 미확정 (기기 재현 필요)

- **G4/H2 — UnifiedSavePath aliasing: 메커니즘 확정(모드 디컴파일), 유령슬롯 인스턴스만 미확정.** 모드가 GetProfileDir를 `profile{id}`로 강제(+ IsRunningModded getter/setter 모두 false 강제) → 모드 활성 중 **런처의 modded 토글이 무력화**되고 modded/unmodded가 물리 수렴. 타이밍: pre-PLAY 핸드셰이크는 모드 로드 전(6슬롯 정상), 게임+백그라운드 sync는 모드 로드 후(수렴) — **모드-로드 경계의 시간적 aliasing**이 교차오염 뿌리. 위험 조합 = UnifiedSavePath 기기 ↔ **표준-modded** 기기(↔unmodded 기기는 오히려 안전). **사용자 폰에 이 모드 설치·퍼블릭에서 활성 확인됨** — 유령 profile2(1550B)의 유력 원인. 구조 fix 곤란(모드가 의도적 붕괴) → 운영 안내: 전 동기화 기기 일괄 적용 또는 미사용. 인스턴스 확정법(재연결 시): Issue36 백업 스냅샷(`Saves/auto/*_conflict/`)에서 profile2/saves/progress.save pull → 모드 ModelId 포함 여부 grep.
- steam B1(mirror-delete 오발), A1 파괴 서브케이스, quota 초과 EResult 실측 — device-test-qa 재현 항목.
- Steam Cloud 파일당 크기 한도/time_stamp 단위 — 1차 자료 없음, 관측 로깅 권고.

### 반증된 가설 (조치 불필요)

- **BaseLib 동적 ModelId 해시 ≠ 세이브 위협**: ModelId는 문자열("CATEGORY.ENTRY")로 직렬화, 런타임 해시 미포함. 미등록 키는 _unknown* 버킷 보존 후 재emit — "Unknown card ID" 경고 무해.
- **ShouldOverwriteCloudWithLocal=true 는 정답**: false면 게임이 blind local→cloud push(issue #4 계열). 현행 유지.
- **압축/platforms_to_sync 구현 정확**(proto 대조) — 손대지 말 것.

## 이번 세션에 이미 조치된 방어 (code 292~296)

| 조치 | 커밋 |
|---|---|
| SavePathCompat(v0.108 시그니처 브리지) + InitSetterEmit 활성화 | 507850b |
| 정보성 다이얼로그 닫기 local-only 오폴백 수정 | 4a408ea |
| AllEncounters pre-Init 가드(#55 softlock 본체) | 99d323e |
| 프로필별 Save Manager UI | bd52fa5 |
| 읽기 순단 Unverified 처리(파괴선택 차단) + GuardC(손상 JSON 업로드 차단) | e784567 |
| 닫기 재다운로드/중복 다운로드 제거 | 607a83c |
| 클라우드 작업 중 UI 전면 잠금 + 재진입 가드, 다이얼로그 가독성 | 23985da |

## 기기 재연결 시 검증 대기 목록

1. code 296 설치 → busy-guard(작업 중 버튼 연타), 다이얼로그 글자 크기, 전 버튼 잠금
2. GuardC 정상 통과 로그(`[Issue36-GuardC] ALLOW(json-ok)`) 확인
3. P0-1 수정 구현 시: PC↔모바일 디싱크 시나리오 재현(수정 전 baseline 확보됨) → 해소 확인
4. G4/H2 유령 슬롯 재현 실험(UnifiedSavePath 온/오프 대조)
