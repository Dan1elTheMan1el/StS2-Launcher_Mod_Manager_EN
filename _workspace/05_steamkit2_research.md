# 05 — SteamKit2 / Steam Cloud Best Practice 리서치

> 목적: Android 런처의 SteamKit2 3.4.0 클라우드 세이브 구현을 공식/커뮤니티 best practice 와 대조하기 위한 근거 수집.
> 조사일: 2026-07-08. 코드 변경 없음(리서치 전용).
> 신뢰도 표기: **[공식]** = Valve/SteamKit 1차 문서, **[역공학]** = 신뢰 가능한 커뮤니티 역공학(GameNative/DoctorMcKay 등), **[추정]** = 근거 부족한 추론, **[자료 없음]** = 못 찾음(추정으로 채우지 않음).

---

## 헤드라인 (핵심 5줄)

1. **[공식·최중요]** 실사고("반쯤 열린 배치") 가설은 Valve 공식 문서로 확인됨. `CompleteAppUploadBatch` 를 호출하지 않으면 **해당 user+app 의 신규 업로드가 수 분간 "too many pending requests" 로 차단**된다 — PC 클라이언트 반복 동기화 실패와 정확히 일치.
2. **[공식]** `CompleteAppUploadBatch` 에는 두 변형이 있다: fire-and-forget **Notification**(`NoResponse`) vs 응답을 받는 **`CompleteAppUploadBatchBlocking`**(Request/Response). Android 5초 백그라운드 flush 제약에서 fire-and-forget 는 전송 전에 프로세스가 정지될 수 있다 → **Blocking 변형을 await 후 disconnect** 해야 안전.
3. **[공식]** `BeginAppUploadBatch` 응답의 `app_change_number` 는 **"currently unused by this API"**. 즉 배치는 충돌 버저닝 수단이 아니라 서버측 업로드 mutex 다. 충돌 판정 상태는 Steam 클라이언트가 로컬에서 관리 → 서드파티(우리)는 이 상태에 개입할 수 없다.
4. **[역공학]** platforms_to_sync=uint.MaxValue(4294967295)와 file_size(압축)/raw_file_size(원본) 분리 매핑은 **프로토콜 정의와 정확히 일치**(proto default 가 4294967295) — 현재 구현이 맞다.
5. **[공식]** SteamKit2 **3.4.0(2026-01-14)이 현재 최신**. 더 올라갈 버전 없음. 3.4.0 이후 cloud/auth 관련 미적용 fix 없음 → 업그레이드로 이 사고를 못 고친다. 코드에서 배치 라이프사이클을 직접 고쳐야 한다.

---

## Q1. AppUploadBatch 시맨틱 — Begin/Complete, 미완료 배치의 영향

### 발견 사실

**[공식]** Steamworks `ICloudService` WebAPI 문서에서 `CompleteAppUploadBatch` 설명 원문:

> "Once all uploads and deletes have been attempted, successful or not, you must call `CompleteAppUploadBatch`. This indicates to Steam that all operations for this batch have been attempted and it will then allow any newly-requested batches immediately."
>
> **"Failing to call `CompleteAppUploadBatch` will result in a time period of several minutes where new upload attempts for this user and app are blocked with a 'too many pending requests' response."**

출처: [ICloudService (Steamworks Documentation)](https://partner.steamgames.com/doc/webapi/icloudservice)

**[공식]** 프로토콜 구조 (steammessages_cloud.steamclient.proto):
- `CCloud_BeginAppUploadBatch_Request`: `appid`, `machine_name`, `repeated files_to_upload`, `repeated files_to_delete`, `client_id`, `app_build_id`
- `CCloud_BeginAppUploadBatch_Response`: `batch_id`(이걸 Complete 에 되돌려줘야 함), `app_change_number`
- **두 개의 Complete 변형**:
  - `CompleteAppUploadBatch` = **Notification → `NoResponse`** (fire-and-forget)
  - `CompleteAppUploadBatchBlocking` = **Request → Response** (확인 응답 있음)
  - 공통 필드: `appid`, `batch_id`, `batch_eresult`(1=성공, 2=실패)

출처: [SteamTracking/Protobufs · steammessages_cloud.steamclient.proto](https://github.com/SteamTracking/Protobufs/blob/master/steam/steammessages_cloud.steamclient.proto)

**[공식]** `app_change_number` 는 응답에 존재하지만 문서상 **"currently unused by this API"**. 배치는 "이 user+app 에 대해 지금 내가 업로드 세트를 소유한다"는 서버측 배타 잠금이며, 충돌 버전 번호가 아니다.

### 우리 구현에 대한 권고 (실사고 직결)

1. **CompleteAppUploadBatch 를 try/finally 로 무조건 호출**하라. 개별 파일 커밋의 성공/실패와 무관하게, 예외/타임아웃/취소가 나도 반드시 배치를 닫아야 한다. 하나라도 실패했으면 `batch_eresult=2`(실패), 전부 성공이면 `1`. — 이것이 "반쯤 열린 배치"를 원천 차단하는 유일한 방법이며, 공식 문서가 명시적으로 요구하는 계약이다.
2. **fire-and-forget Notification 대신 `CompleteAppUploadBatchBlocking`(Request/Response)을 사용하고 응답을 await** 한 뒤에만 disconnect/백그라운드 전환하라. 현재 "백그라운드 전환 시 5초 flush" 는 Notification 이 실제로 소켓에 실려 나가기 전에 프로세스가 정지될 수 있는 정확한 조건이다. Blocking 변형은 서버가 배치를 닫았다는 확인을 준다.
3. **배치 완료를 flush 시퀀스의 최우선·최후 단계로 고정**하라. 5초 예산 안에서 "열린 배치가 있으면 무조건 먼저 닫는다"를 보장. 열린 batch_id 를 영속화(예: user:// 작은 파일)해 두고, 다음 세션 시작/EnsureConnected 직후 미완료 배치를 `CompleteAppUploadBatchBlocking(eresult=2)` 로 청소하는 복구 경로를 추가하면 "수 분 차단" 잔존 시간을 스스로 해제할 수 있다.
4. batch 를 **파일 하나라도 실제로 업로드할 때만 열어라**. delete-only/no-op 경로에서 배치를 열고 안 닫는 실수 방지.

---

## Q2. Steam Cloud 충돌 해소 모델

### 발견 사실

**[공식·부정적]** Valve 의 [Steam Cloud 기능 문서](https://partner.steamgames.com/doc/features/cloud)는 충돌 해소 알고리즘을 **공개 문서화하지 않는다**. 문서가 다루는 것은 Auto-Cloud 복제, Dynamic Cloud Sync(suspend 시 업로드), `BeginFileWriteBatch`/`EndFileWriteBatch` 는 "hints" 라는 서술 수준까지다. 로컬 vs 리모트 change list 비교 방식, 충돌 다이얼로그 트리거 조건, manifest 구조, 타임스탬프 조정 규칙, 미완료 sync 복구 절차는 **문서에 없음 [자료 없음]**.

**[역공학]** 실제 알고리즘은 GameNative(Android Steam 런처)의 역공학 구현에서 재구성 가능:
- Steam 클라이언트는 로컬에 **`change_numbers` / `file_change_lists`** 테이블로 "마지막으로 동기화된 상태"를 영속화.
- 충돌 판정: `localAppChangeNumber < cloudAppChangeNumber` **그리고** 로컬 파일이 마지막 동기 상태와 다름(양쪽이 diverge) → 충돌 → 사용자 개입 필요.
- 파일 변경 감지는 **타임스탬프가 아니라 SHA-1 해시 비교**로 한다.
- 충돌 시 타임스탬프는 사용자에게 "어느 쪽을 유지할지" 보여주는 UI 힌트로만 쓰인다.

출처: [GameNative · Cloud Save Synchronization (DeepWiki)](https://deepwiki.com/utkarshdalal/GameNative/5.4-cloud-save-synchronization), [ValveSoftware/steam-for-linux#13091 (충돌 재현 리포트)](https://github.com/ValveSoftware/steam-for-linux/issues/13091)

### 서드파티(우리)가 지켜야 할 invariant

- **우리는 PC Steam 클라이언트의 로컬 change-number/sync-state DB 에 접근/기여할 수 없다.** 우리가 클라우드에 파일을 쓰면 PC 클라이언트 입장에선 "리모트 change number 가 올라갔는데 로컬 마지막-동기 상태와 안 맞는" 상태가 되어 충돌 다이얼로그가 뜰 수 있다. 이는 우리 잘못이 아니라 구조적 한계다 — 단, **배치를 항상 완결(Q1)** 하면 최소한 "반쯤 쓴 파일 세트" 로 인한 반복 실패는 없앤다.
- **SHA-1 기반 감지**를 존중하라: 내용이 실제로 바뀐 파일만 업로드(불필요한 재업로드가 change number 를 올려 PC 쪽 충돌 유발). 우리 캐시(EnumerateUserFiles 메타)로 sha/size 를 비교해 no-op 업로드를 스킵할 것.
- 클라우드에 쓰는 파일 세트는 **게임이 관리하는 논리적 세트와 원자적으로 일치**해야 한다(부분 업로드로 상호 불일치한 세이브 조합을 남기지 말 것).

---

## Q3. SteamKit2 연결 수명 best practice

### 발견 사실

**[공식]** SteamKit 은 **자동 재접속하지 않는다**. `DisconnectedCallback` 을 받으면 사용자 코드가 직접 `Connect()` 를 다시 불러야 한다. 공식 이슈/샘플의 권장 패턴은 disconnect 핸들러에서 **ExponentialBackoff** 로 재시도.
출처: [SteamRE/SteamKit#236](https://github.com/SteamRE/SteamKit/issues/236), 공식 Sample 들.

**[공식]** 콜백 루프: `CallbackManager.RunWaitCallbacks(timeout)` 는 콜백이 큐에 없으면 timeout 만큼 블록. 3.0.0 부터 async 대안 `CallbackManager.RunWaitCallbackAsync` / `SteamClient.WaitForCallbackAsync` 존재. `RunCallbacks` 는 3.0.0 부터 처리 여부 bool 반환.
출처: [changes.txt](https://github.com/SteamRE/SteamKit/blob/master/SteamKit2/SteamKit2/changes.txt)

**[공식]** ServerList 캐싱: `SmartCMServerList` 는 마지막 refresh 가 **7일** 초과면 WebAPI 로 자동 갱신(3.0.0). 온디맨드 접속 모델이라면 ServerList 를 디스크에 영속화해 콜드 스타트 접속을 빠르게 하는 것이 권장.

**[공식·연결 신뢰성 관련 최근 fix]**:
- 3.3.0: "Post client disconnection callback before cancelling async jobs" — disconnect 시 진행 중 async job(=우리 업로드 RPC)이 취소되기 **전에** disconnect 콜백이 먼저 온다. 즉 우리 write 큐가 도중에 disconnect 되면 진행 중 배치 RPC 가 취소될 수 있음 → Q1 의 finally-완결이 더 중요.
- 3.2.0: "`AsyncJob` will now instantly fail if not connected to Steam" — 끊긴 상태에서 RPC 를 던지면 즉시 실패. 온디맨드 EnsureConnected 가 완전히 established 된 뒤에만 배치를 시작해야 한다.
- 3.0.2: "Fixed handling on tasks to reduce chance of deadlocking sync-over-async consumers" — sync-over-async 데드락 주의(우리 단일 백그라운드 write 스레드가 `.Result`/`.Wait()` 로 async RPC 를 블로킹하면 위험).

### EResult 재시도 분류

**[공식·부분]** `EResult.TryAnotherCM` 은 명시적으로 "다른 CM 서버로 재접속하라"는 신호(재시도 가능). `ServiceUnavailable`, `Timeout`, `TryAnotherCM` 류는 일반적으로 재시도 가능; `AccessDenied`, `InvalidPassword`, `Banned`, `Expired` 류는 재시도 불가(재인증/사용자 개입). **단, "어떤 EResult 가 재시도 가능인가"를 규정한 단일 SteamKit 공식 표는 못 찾음 [자료 없음]** — 위 분류는 관례적 판단이다.

### 우리 구현 권고

- 온디맨드 disconnect/EnsureConnected 자체는 정상적 패턴이나, **EnsureConnected 는 "logged on(LoggedOnCallback 성공)"까지 완료를 기다린 뒤** 배치를 시작하라(3.2.0 즉시실패 회피).
- disconnect 핸들러에 **ExponentialBackoff** 재접속(무한 즉시 재시도 금지). idle-timeout 자동 disconnect 와 에러 disconnect 를 구분해 후자만 백오프 재접속.
- write 스레드에서 async RPC 를 `.Result` 로 블로킹하지 말 것(3.0.2 데드락). 큐 컨슈머를 async 로 유지하거나 전용 SynchronizationContext 없이 `ConfigureAwait(false)`.
- ServerList 를 user:// 에 영속화(콜드 스타트 가속, 모바일 재시작 잦음).

---

## Q4. refresh token 수명·갱신

### 발견 사실

**[역공학]** Steam refresh token 의 **고정 만료 기간은 공식적으로 명시되지 않음**. DoctorMcKay(Steam auth 역공학 권위자) 포럼: 실측상 **"200일 넘게 살아있는"** 사례(Steam Guard 없는 계정), 정확한 값은 **JWT 를 디코드해 `exp` 클레임을 읽으라**. 토큰은 Valve 재량으로 **서버측 무효화** 가능(IP 변화 등). access token 은 훨씬 짧고 refresh token 으로 재발급.
출처: [Access tokens and refreshtoken lifetime — DoctorMcKay](https://dev.doctormckay.com/topic/4607-access-tokens-and-refreshtoken-lifetime/)

**[역공학]** 갱신 메커니즘: `GenerateAccessTokenForApp` 요청에 **`renewal_type`(ETokenRenewalType: `None=0`, `Allow=1`)** 필드가 있고, **응답이 `access_token` 과 함께 새 `refresh_token` 을 반환**할 수 있다. 즉 access token 재발급 시 `renewal_type=Allow` 를 주면 만료가 임박한 refresh token 을 **롤링 갱신**할 수 있다.
출처: [steammessages_auth.steamclient.proto (DoctorMcKay)](https://github.com/DoctorMcKay/node-steam-session/blob/master/protobufs/steammessages_auth.steamclient.proto), [New Steam login flow · SteamKit#1125](https://github.com/SteamRE/SteamKit/issues/1125)

### 우리 구현 권고

- 저장된 refresh token 의 **`exp` 를 디코드해 사전 점검**하고, 만료 임박(예: 7~14일 이내)이면 로그인 성공 경로에서 `GenerateAccessTokenForApp(renewal_type=Allow)` 로 **롤링 갱신 후 새 refresh token 을 영속화**하라. 이러면 사용자가 오래 안 켜도 재로그인 UX 를 최소화.
- 갱신/로그온이 만료로 실패(EResult Expired/AccessDenied)하면 **명확한 재로그인 프롬프트** UX. 자동 무한 재시도로 잠금 유발 금지.
- SteamKit2 3.x 가 이 RPC 를 C# 헬퍼로 직접 노출하지 않으면 `SteamUnifiedMessages` 로 `IAuthenticationService.GenerateAccessTokenForApp` 를 직접 호출 가능(우리가 이미 CCloud 를 그렇게 쓰는 것과 동일 패턴).

---

## Q5. Cloud API 사용 선례

### 발견 사실

**[역공학·강함] GameNative (utkarshdalal/GameNative)** — Android 에서 Steam 게임+클라우드 세이브를 구동하는 프로젝트로 우리와 가장 직접적으로 유사:
- 전체 전송을 `beginAppUploadBatch()`/`completeAppUploadBatch()` 로 감싸고, 파일마다 `beginFileUpload()`/`commitFileUpload()` + **HTTP PUT**.
- machine_name = `SteamUtils.getMachineName()`, build id = `appInfo.branches["public"]?.buildId` 로 배치 메타 구성.
- 충돌은 change number + SHA-1 + 로컬 sync-state DB(위 Q2).
- 다운로드는 hash 검증 + 재시도, "hash mismatch after retries" 시 `DownloadFail`.
- **단, 문서상 배치 완료를 try/finally 로 무조건 보장하는지는 불명**(우리가 Q1 에서 더 엄격히 할 여지).
- platforms_to_sync 필터는 다루지 않음(Steam 전용 구현).

출처: [GameNative · Cloud Save Synchronization](https://deepwiki.com/utkarshdalal/GameNative/5.4-cloud-save-synchronization)

**[역공학·약함] CloudKit (unknownv2/CloudKit)** — SteamKit 으로 Steam Cloud 세이브를 CLI 로 관리. 다만 커밋 16개·릴리스 없음의 **오래된/소규모** 프로젝트로, 배치/타임스탬프/충돌 처리 세부는 문서화 안 됨. 참고용.
출처: [unknownv2/CloudKit](https://github.com/unknownv2/CloudKit)

**[공식] proto 정합성 확인**:
- `platforms_to_sync` proto **default 가 정확히 4294967295** → 우리 uint.MaxValue 는 "모든 플랫폼 동기화" 로 프로토콜 기본값과 일치. (크로스플랫폼 세이브 의도에 맞음.)
- `ClientBeginFileUpload_Request` 와 `ClientFileDownload_Response` 모두 `file_size` 와 `raw_file_size` 를 **별도 필드로** 가짐 → 우리의 file_size=압축 / raw_file_size=원본 매핑은 프로토콜 의미와 일치.

### 우리 구현 권고

- GameNative 를 참조 구현으로 신뢰하되, **배치 완결 보장(try/finally + Blocking) 은 GameNative 보다 엄격하게** 가져가라(우리 실사고의 핵심).
- machine_name/app_build_id 를 GameNative 처럼 채워 PC 클라이언트 충돌 UI 의 "어느 기기" 표시를 정확히.

---

## Q6. quota / 한도

### 발견 사실

**[공식]** Steam Cloud quota 는 **user 당·game 당(per-user-per-game)** 으로 App Admin 의 "Byte quota per user" 와 "Number of files allowed per user" 두 값으로 설정·강제된다. App Admin 이 받는 최대 byte quota 는 **100,000,000,000 bytes(약 93.13 GiB)**.
출처: [Steam Cloud (Steamworks Documentation)](https://partner.steamgames.com/doc/features/cloud)

**[커뮤니티]** 초과 시 사용자에겐 "You have exceeded your Steam Cloud quota..." 메시지가 노출됨(파일/용량 정리 요구).
출처: [Steam Community — exceeded Steam Cloud quota](https://steamcommunity.com/discussions/forum/7/6670425060415451379/)

**[자료 없음]**: quota 초과 시 CCloud 업로드가 반환하는 **정확한 EResult 코드**(예: LimitExceeded/QuotaExceeded), 파일당 최대 크기, 타임스탬프 정밀도(초 vs ms)의 확정 값은 1차 문서에서 못 찾음. proto 의 `time_stamp` 는 `uint64` 이나 단위 확정 근거는 미확보(관례상 Unix epoch seconds로 보이나 **미검증**).

### 우리 구현 권고

- 업로드 커밋 실패(`file_committed=false`) / begin 실패 시 **EResult 를 로깅**해 quota 초과 케이스를 실측 수집(현재 문서로는 코드 확정 불가하니 우리가 관측으로 채워야 함).
- StS2 세이브는 소용량이라 quota 초과 가능성은 낮지만, **로컬 풀트리 스냅샷(local backup)** 을 클라우드에 올리지 않도록(그건 로컬 전용) 경계 유지 — 클라우드엔 게임이 관리하는 세이브 세트만.
- `time_stamp` 단위는 **PC 클라이언트가 올린 기존 파일의 값과 대조해 경험적으로 확정**한 뒤 비교 로직에 반영(추정으로 하드코딩 금지).

---

## Q7. SteamKit2 3.4.0 이후 cloud/auth fix — 업그레이드 가치

### 발견 사실

**[공식]** **3.4.0(2026-01-14 NuGet 발행)이 현재 최신 안정판.** 그보다 새 버전 없음(조사일 2026-07-08 기준). 우리는 이미 최신.
출처: [NuGet · SteamKit2](https://www.nuget.org/packages/SteamKit2), [SteamRE/SteamKit Releases](https://github.com/SteamRE/SteamKit/releases)

**[공식]** 3.4.0 변경점(cloud/auth 무관 위주): `UserCountryCode` 추가, privacy enum, `DepotChunk.AdlerHash` 이동, KeyValue LINQ 할당 제거, `GameID.GetHashCode` fix, `DepotManifest` LinkTarget 복호화 fix, **.NET 10 타깃**, `AccountInfoCallback` 의 deprecated Facebook 필드 제거(breaking). **cloud 세이브 배치/충돌 관련 fix 는 없음.**

3.0~3.3 중 우리와 관련된 연결 신뢰성 fix(위 Q3): 3.3.0 disconnect-콜백 순서, 3.2.0 async job 즉시실패, 3.0.2 데드락 완화 — **이미 3.4.0 에 포함**.

### 우리 구현 권고

- **업그레이드로 이 사고를 못 고친다** — 3.4.0 이 최신이고 cloud 배치 관련 미적용 fix 가 없다. 근본 해결은 **우리 코드의 배치 라이프사이클(Q1)** 이다.
- .NET 10 타깃 전환은 우리 Android 빌드 타깃과의 호환을 별도 확인(범위 밖, 참고).

---

## 우리 구현 위험 대조 요약 (감사팀 리스크 레지스터 연결용)

| 우리 구현 요소 | best practice 대조 | 판정 |
|---|---|---|
| 개별 파일 commit 되나 AppUploadBatch 미완결(실사고 가설) | 공식: 미완결 = user+app 수 분 차단("too many pending requests") | **확정 위험. try/finally + Blocking 완결 필수** |
| CompleteAppUploadBatch 를 fire-and-forget 로? | 공식: Blocking 변형 존재, 응답 확인 가능 | **Blocking 으로 전환 권고** |
| 백그라운드 5초 flush 중 배치 완료 | Android 프로세스 정지 조건과 겹침 | **배치 완결을 flush 최우선/최후로 고정 + 열린 batch_id 영속·복구** |
| platforms_to_sync=uint.MaxValue | proto default=4294967295 | **정상** |
| file_size(압축)/raw_file_size(원본) | proto 별도 필드, 의미 일치 | **정상** |
| refresh token 재로그인 | 만료 미명시(~200일+), renewal_type=Allow 롤링 갱신 가능 | **exp 사전점검 + 롤링 갱신 권고** |
| 온디맨드 disconnect/EnsureConnected | SteamKit 무자동재접속, ExponentialBackoff 권장 | **LoggedOn 완료 대기 + 백오프 재접속** |
| SHA/메타 캐시로 no-op 업로드 스킵 | Steam 은 SHA-1 로 변경 감지, 불필요 업로드가 PC 충돌 유발 | **내용 변경분만 업로드 권고** |
| quota 초과 처리 | 정확한 EResult 미문서화 | **관측 로깅으로 실측 수집** |
| SteamKit2 3.4.0 | 최신, cloud fix 없음 | **업그레이드 무의미, 코드 수정으로 해결** |

---

## 출처 목록

- [ICloudService (Steamworks Documentation) — 공식, 배치 미완결 경고 원문](https://partner.steamgames.com/doc/webapi/icloudservice)
- [SteamTracking/Protobufs · steammessages_cloud.steamclient.proto — CCloud 필드 정의](https://github.com/SteamTracking/Protobufs/blob/master/steam/steammessages_cloud.steamclient.proto)
- [Steam Cloud (Steamworks Documentation) — 기능/quota](https://partner.steamgames.com/doc/features/cloud)
- [GameNative · Cloud Save Synchronization (DeepWiki) — Android 역공학 참조 구현](https://deepwiki.com/utkarshdalal/GameNative/5.4-cloud-save-synchronization)
- [SteamKit changes.txt — 버전별 변경점](https://github.com/SteamRE/SteamKit/blob/master/SteamKit2/SteamKit2/changes.txt)
- [SteamRE/SteamKit Releases — 3.4.0 최신 확인](https://github.com/SteamRE/SteamKit/releases)
- [NuGet · SteamKit2 — 3.4.0(2026-01-14) 최신](https://www.nuget.org/packages/SteamKit2)
- [Access tokens and refreshtoken lifetime — DoctorMcKay 포럼](https://dev.doctormckay.com/topic/4607-access-tokens-and-refreshtoken-lifetime/)
- [steammessages_auth.steamclient.proto (DoctorMcKay) — renewal_type/ETokenRenewalType](https://github.com/DoctorMcKay/node-steam-session/blob/master/protobufs/steammessages_auth.steamclient.proto)
- [New Steam login flow · SteamKit#1125 — 신 로그인/토큰 플로우](https://github.com/SteamRE/SteamKit/issues/1125)
- [SteamRE/SteamKit#236 — 자동 재접속 없음/DisconnectedCallback](https://github.com/SteamRE/SteamKit/issues/236)
- [unknownv2/CloudKit — 구형 참고 구현](https://github.com/unknownv2/CloudKit)
- [ValveSoftware/steam-for-linux#13091 — 클라우드 충돌 재현 리포트](https://github.com/ValveSoftware/steam-for-linux/issues/13091)
- [Steam Community — exceeded Steam Cloud quota 메시지](https://steamcommunity.com/discussions/forum/7/6670425060415451379/)
