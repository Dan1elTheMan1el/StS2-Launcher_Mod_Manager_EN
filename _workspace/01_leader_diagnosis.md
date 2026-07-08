# 진단 (2026-07-07, 리더 직접) — SaveManager 크리티컬 + issue #55 잔존

## 증상 1: Save Manager "string 인자 not found" (사용자 실기기 보고)

- 실기기(Fold 7, launcher v0.3.31 code 289, 게임 v0.108.0) SAVE MANAGER 진입 시 콘솔:
  `[Cloud] Save Manager error: Method not found: string MegaCrit.Sts2.Core.Saves.Managers.ProgressSaveManager.GetProgressPathForProfile(int)`
- catch 위치: `LauncherController.cs:304` (OpenSaveSyncDialogAsync 호출부)
- 증거: `.repro/issue58/device_launcher.log` (22:09:06.374), `screen_savemanager.png`

### 근본 원인 (확정)

게임 v0.108.0 (commit 58694f64, 2026-07-02) 에서 save-path 정적 헬퍼 3종에 `bool? forceModState = null` 추가:

| 메서드 | 구(≤0.107) | 신(0.108.0) |
|---|---|---|
| ProgressSaveManager.GetProgressPathForProfile | `string (int)` | `string (int, bool?)` |
| RunSaveManager.GetRunSavePath | `string (int, string)` | `string (int, string, bool?)` |
| PrefsSaveManager.GetPrefsPath | `string (int)` | `string (int, bool?)` |

확인 방법: PC Steam 설치본(`C:\Program Files (x86)\Steam\...\Slay the Spire 2`, buildid 24032229, release_info v0.108.0) sts2.dll 을 ilspycmd 디컴파일, 구버전 참조본(`upstream/godot-export/.../sts2.dll`, 6/8자)과 비교.

C# 기본 인자 = 컴파일타임 설탕 → 구 시그니처로 컴파일된 IL 호출은 MissingMethodException. **호출을 포함한 메서드의 JIT 시점**에 터짐 → Save Manager 버튼뿐 아니라 CloudSyncDecisions(동기화 결정 엔진)/CloudSyncCoordinator/Issue7Diagnostics 도 동일 파급(데이터 안전 영역).

직접 호출 13곳: CloudSyncDecisions.cs:128,138 / Issue7Diagnostics.cs:79-81 / CloudSyncCoordinator.cs:313-316 / LauncherPatches.cs:493,577,583,589

### 수정 방향 (Fix A)

참조 DLL 교체 금지(≤0.107 브랜치 전환 호환 유지) → 런타임 리플렉션 브리지 `SavePathCompat` (구 시그니처 우선, 실패 시 신 시그니처+null). steam-integration-specialist 구현 중.

## 증상 2: issue #55 잔존 softlock (리포터 abhbh1235 코멘트 2026-07-07T11:42Z)

- 리포터+자체 기기 공통 재현: `[STS2Mobile] InitSetterEmit: MonoMod.Utils.Cil.ILGeneratorShimExt not found — init-setter fix inactive`
- **v0.3.31 의 PR #57 root fix 가 릴리즈 전체에서 무동작이었음.**

### 근본 원인 (확정) — 로드 타이밍

- `InitSetterEmitPatches.Apply` (ModEntry.cs:95) 는 첫 `harmony.Patch` (첫 "Patched" 로그 22:05:27.785) 이전(22:05:27.539)에 실행됨.
- MonoMod.Utils.dll 은 0Harmony 의 첫 DMD 생성 시 지연 로드 → 이 시점 AppDomain 에 없음.
- `AccessTools.TypeByName` 은 로드된 어셈블리만 검색 → null.

### 리포터 가설 반증

"게임 자체 MonoMod.Utils.dll (data_sts2_windows_x86_64) 이 런처 번들을 오버라이드" — **반증**:
1. 게임 v0.108.0 은 MonoMod.Utils 를 배포하지 않음 (0Harmony/MonoMod.Backports/MonoMod.ILHelpers 만).
2. GodotApp.java bclNames 가드(dotnet_bcl asset 이름 = 보호 목록)가 게임 dll 의 BCL 덮어쓰기를 차단, 매 부팅 BCL 재복사.
3. 번들 MonoMod.Utils.dll (md5 6da8bc91876f5ede0d2ed03fc1d96779) 에 ILGeneratorShimExt/DynEmit 존재 (바이너리 확인).

### 수정 방향 (Fix B)

TypeByName null 이면 `typeof(Harmony).Assembly.GetReferencedAssemblies()` 의 MonoMod.Utils AssemblyName 으로 Assembly.Load 후 재조회. harmony-patch-specialist 구현 중.

## 추가 확정 사실 (구현 단계)

- **MemberRef 전수 감사** (tools/memberref-audit, 신규 편입): v0.3.31 STS2Mobile.dll 은 sts2-scoped MemberRef 50건 중 **4건 missing** — grep 으로 찾은 3종 외에 `RunHistorySaveManager.GetHistoryPath(int)` (CloudSyncCoordinator.cs:334) 추가 발견. 수정 빌드는 구/신 sts2.dll 양쪽 46건 전부 0 missing.
- **HextechRunes 는 v0.108 게임과 자체 비호환**: 게임이 `CreatureCmd.Damage` 전 오버로드에 `CardPlay? cardPlay` 파라미터 추가 → 모드의 `HextechHookReflection.RequireMethod` 가 구 시그니처 조회 실패 → `InstallDamageCommandHooks` 예외로 모드 초기화 중단. 런처 수정으로 해결 불가(모드 저자 업데이트 필요). PC v0.108 에서도 동일하게 깨질 것.
- **22:23 앱 재시작은 정상 종료 플로우**: "Quit confirmed" → 클라우드 flush → `NGame.Quit intercepted, restarting app`. 크래시/softlock 아님.
- 사용자 PLAY 세션(BaseLib+HextechRunes+스킨 4종+UnifiedSavePath)에서 ACT.OVERGROWTH softlock 은 **재현되지 않음** — BaseLib act 키 생성 정상, ModelDb.Init 트레이스 정상 완료. 리포터의 softlock 은 더 많은/다른 모드 조합일 가능성.

## 기기 검증 중 추가 발견 (사용자 보고 → 수정 반영)

- **Cancel→local-only 오적용 버그**: `HandleConflictAsync` Cancel 분기(LauncherPatches.cs:450)는 첫 PLAY 진짜-충돌 보류용 보호 장치인데, Save Manager 버튼 흐름이 decision=Identical 정보성 다이얼로그에도 같은 분기를 재사용 → 닫기만 해도 `_cloudCacheReady=false`. 현 아키텍처에선 사실상 no-op(플래그 소비처는 ConstructDefaultPrefix뿐이고 inline preload가 되돌림)이지만 오해 로그 + 미래 지뢰 → Identical/NoData Cancel은 무변이로 수정.
- **Save Manager 열기당 중복 다운로드**: 같은 파일(progress.save 등)을 비교/요약 단계가 각각 받아 2회씩 다운로드. 읽기 전용이라 안전 무관, 효율 개선 후보로 별도 이슈화 예정 (이번 브랜치 미수정).
- Save Manager 열 때마다 비교 대상(progress/current_run × profile/mod-state, ~6-9파일) 다운로드는 설계 의도(내용 비교 필수, issue #7 교훈). 108파일 전체 다운로드는 충돌 적용 전 1회성 백업(issue #36)으로 정상.

## issue #55 근본 트리거 확정 (퍼블릭 v0.107.1 재현, Fix C)

퍼블릭 브랜치(v0.107.1, commit 59260271)에서 사용자가 "PLAY→싱글플레이 후 커스텀/일반/일일도전 버튼 무반응" 재현 → **드디어 softlock 본체를 스택트레이스로 확정**:

```
22:58:58 [Issue55-Trace] AllEncounters read BEFORE Init completed (initStarted=False)
         Stack: UnlockState..cctor ← HextechRunes.HextechInspectHooks.Install (UnlockState.Relics 읽음)
22:58:58 [WARN] [HextechRunes] Inspect hook skipped: UnlockState.Relics: TypeInitializationException
22:59:00 [Issue55-Trace] ModelDb.Init prefix/body (2초 늦게 실행)
22:59:07 TypeInitializationException at ProgressSaveManager.GenerateUnlockState()
         ---> KeyNotFoundException 'ACT.OVERGROWTH' at ...get_AllEncounters_Patch1 → UnlockState..cctor()
```

**인과 사슬:** HextechRunes 가 자기 init 중 `UnlockState.Relics` 를 읽음 → `UnlockState..cctor` 조기 기동 → cctor 의 `static readonly all` 필드 초기화가 `ModelDb.AllEncounters` 읽음 → 이때 ModelDb.Init 이 아직 안 돌아 base act(OVERGROWTH) 미등록 → `Get<Overgrowth>()` KeyNotFound → **cctor 예외 영구 캐시(.NET 타입 오염)** → 이후 모든 UnlockState 접근(`GenerateUnlockState` = `new UnlockState(Progress)`, 인스턴스 생성도 cctor 요구)이 캐시된 예외 재throw → 모드선택 화면이 unlock state 못 만들어 버튼 전체 무반응.

- 메모리 노트의 "#3 트리거 미확정" 가설(ModelDb.Init 전 UnlockState cctor 조기 기동)이 **정확히 실증됨**. 계측(`[Issue55-Trace]`)이 표적을 잡아냄.
- `UnlockState(ProgressState)` ctor 는 `ModelDb.GetByIdOrNull`(안전) 사용 + `all` 미참조 → cctor 만 정상 완료시키면 플레이어 unlock state 는 정확히 복구됨.

**Fix C (InitSetterEmitPatches.AllEncountersPrefix 관찰→가드 승격):** `!_initStarted` 이면 빈 `EncounterModel[]` 반환 + 원본 getter skip → cctor 가 throw 없이 완료 → 오염 방지. 유일한 저하는 in-memory `all`/`none` 센티넬의 encounter 데이터(세이브·플레이어 진행과 무관, 모드 세션 한정). 게임 v0.108/v0.107 공통 적용.

## 기타 관찰 (미조치)

- 부팅 중 `NInputManager.Init()` NullReferenceException (TaskHelper.LogTaskExceptions 경유, 22:05:32.350) — 별건, 원인 미조사. 모드 유무 상관관계 미확인.
- 기기 모드 셋: BaseLib(7/7 갱신), HextechRunes(7/7 갱신), DefectSkin_AD, Mesugaki, necrobinderSkin, silentSkin, UnifiedSavePath(루즈 dll/json/pck)
- `gh issue view` 가 출력 없이 exit 0 하는 현상 (gh api 는 정상) — 도구 이슈, 별건.

## 빌드/검증 계획

- 워크트리 `D:\git\StS2-LMM-fix55`, 브랜치 `fix/issue-55-v0108-save-path-compat`, 필수 입력물 6종 복사 완료.
- 빌드: 리더 직접 (version_code 290), dex DotnetProxyTrustManager==2 + STS2Mobile.dll md5 대조.
- MemberRef 전수 감사: STS2Mobile.dll 의 sts2-scoped MemberRef 전부를 신 sts2.dll 과 대조 (추가 시그니처 파손 유무 확정).
- 디바이스 검증 4항목 (task #4): DynEmit patched 로그 / Save Manager 무에러 / PLAY 회귀 / char select softlock 시나리오.
