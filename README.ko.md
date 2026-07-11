# StS2 Launcher Mod Manager

[English](README.md) | **한국어**

**Slay the Spire 2** 를 안드로이드에서 실행하는 런처 + 모드 매니저입니다. 커스텀 Godot 4.5.1 엔진(.NET/Mono) 위에서 Harmony 런타임 패칭으로 데스크톱 게임을 모바일에 맞게 적응시킵니다. Steam 로그인, 게임 다운로드, 창작마당(Workshop) 모드 구독, 세이브 관리까지 전부 폰 안에서 처리됩니다.

**현재 릴리즈: [v0.3.34](https://github.com/iunius612/StS2-Launcher_Mod_Manager/releases/latest)** (versionCode 314)

> **📖 사용설명서** — 설치부터 모드/세이브 관리까지 스크린샷과 함께 설명하는 단계별 가이드: **[docs/USER_GUIDE.md](docs/USER_GUIDE.md)**

> **포크 안내** — 이 프로젝트는 [Ekyso/StS2-Launcher](https://github.com/Ekyso/StS2-Launcher) 의 커뮤니티 포크입니다. 업스트림 런처의 모드 로더가 최신 게임 빌드에서 작동하지 않게 되어, 이 포크는 게임 업데이트마다 모드 로딩 호환성을 유지하면서 창작마당 지원·세이브 안전장치·모바일 UX 개선을 추가합니다. [업스트림과의 차이](#업스트림과의-차이) 참조.

> **면책** — 비공식 커뮤니티 프로젝트입니다. Slay the Spire 2 는 Mega Crit Games 가 개발·배급합니다. Slay the Spire 2 를 소유한 유효한 Steam 계정이 필요하며, 게임 파일은 인증 후 Steam 에서 직접 다운로드됩니다. 이 저장소에는 게임 에셋이 포함되어 있지 않습니다.

## 시작하기 (플레이어용)

1. [Releases 페이지](https://github.com/iunius612/StS2-Launcher_Mod_Manager/releases/latest)에서 최신 `StS2Launcher-vX.Y.Z.apk` 를 받아 설치합니다.
2. 실행 후 "모든 파일 액세스" 권한을 부여하고 Steam 계정으로 로그인합니다 (Steam Guard 2FA 지원).
3. Steam 브랜치를 선택해 게임(~3GB)을 다운로드하고 **PLAY** 를 누릅니다.

스크린샷이 포함된 전체 안내는 [사용설명서](docs/USER_GUIDE.md)에 있습니다. 포크 버전 간 업그레이드는 APK 덮어쓰기 설치로 충분하며 세이브·로그인·게임 페이로드가 모두 보존됩니다.

## 업스트림과의 차이

2026년 4월 `Ekyso/StS2-Launcher` 에서 분기했습니다. 아래는 전부 포크에서 추가된 것이며, 버전별 상세 내역은 [Releases 페이지](https://github.com/iunius612/StS2-Launcher_Mod_Manager/releases)에 있습니다.

### 게임 업데이트를 따라가는 모드 로딩

- 업스트림의 리플렉션 기반 모드 로더는 현재 게임 빌드에서 크래시합니다 (private 필드명 변경). Harmony IL 트랜스파일러로 게임 자체 모드 스캐너를 `/storage/emulated/0/StS2LauncherMM/Mods/` 로 리다이렉트하는 방식으로 교체했습니다.
- 이후의 파괴적 게임 업데이트에도 계속 호환 유지: v0.107 async lowering 변경 (issue #47), v0.108 `ModelDb` 계약 변경 (issue #53), v0.108 세이브 경로 헬퍼 시그니처 변경, 그리고 BaseLib 3.3.x 를 살린 init-setter 런타임 IL 재emit 일반 수정 (issue #55).
- `tools/memberref-audit` 가 새 게임 빌드 배포 전에 IL 패치들을 정적으로 감사해, 시그니처 파손을 사용자가 겪기 전에 잡아냅니다.

### Steam 창작마당 지원 (Mod Hub)

v0.3.34 (issue #58) 추가 — **MOD MANAGER** 버튼이 인앱 Mod Hub 를 엽니다:

- **WORKSHOP 탭** — 정렬·태그 필터·무한 스크롤로 창작마당 검색; URL/ID 붙여넣기로 unlisted 모드 조회; 설명/변경내역/토론/댓글 상세페이지.
- **SUBSCRIBED 탭** — 구독이 Steam 계정과 동기화되고 다운로드·업데이트 감지·의존성 안내가 자동으로 처리됩니다. 구독 해제 시 기기에서 삭제.
- **모드 보관 (DISABLE)** — 삭제 없이 게임에서만 숨김 (`ModsDisabled/` 로 이동, ENABLE 로 즉시 복구, 재다운로드 없음).
- 다운로드는 SteamKit2 `PublishedFile` RPC + Steam depot CDN 사용 — PC 도 Steamworks SDK 도 필요 없습니다.

### 세이브 데이터 안전장치

- **클라우드 세이브 파괴 수정 (issue #4)** — 업스트림 런처는 클라우드 파일 캐시가 로드되기 전에 실제 Steam Cloud 진행도를 새 기본 세이브로 조용히 덮어쓸 수 있었습니다. 이제 클라우드 스토어는 캐시가 준비될 때까지 블로킹하고, 준비가 안 되면 로컬 전용 모드로 폴백합니다.
- **Save Manager (issues #4/#7)** — 타임스탬프 기반 무언 동기화를 명시적 충돌 해결 다이얼로그로 교체: 로컬/클라우드 요약 카드 (진행 중 런 포함) 를 나란히 보여주고 사용자가 로컬 유지 / 클라우드 유지를 직접 선택합니다. PLAY 마다 자동 검증 + **SAVE MANAGER** 버튼으로 수시 확인.
- **클라우드 쓰기 가드 + Local Backup (issue #36)** — 비어 있거나 비정상적인 쓰기를 단일 클라우드 업로드 경로에서 차단하고, 세이브 전체 트리 스냅샷을 수동 (**LOCAL BACKUP** 버튼, 무기한 보존) 및 자동 (클라우드 핸드셰이크마다, FIFO 상한) 으로 생성해 잘못된 동기화 선택도 복구할 수 있습니다.

### 런처 편의 기능

- **자체 업데이트 (issue #12)** — `CHECK GAME UPDATE` / `CHECK LAUNCHER UPDATE` 버튼 분리, 부팅 시 자동 업데이트 확인, 인앱 APK 다운로드 + 설치 흐름.
- **Steam 브랜치 선택** — 다운로드마다 `public`, `public-beta` 등 선택 가능; 브랜치 전환 시 delta 손상을 피하려 전체 재다운로드.
- **모바일 UX** — 센서 기반 가로 회전, 폴드/언폴드·분할화면·세로 비율 창·21:9 대응 레이아웃 리플로우, 플링 지원 터치 스크롤 재작성, 뒤로가기 가드, 가상 키보드 대응 패널.
- **adb 없는 진단** — Debug 토글이 logcat 을 `StS2LauncherMM/Logs/` 에 자동 캡처 (자동 로테이션) — 사용자가 이슈에 로그를 바로 첨부할 수 있습니다.

### 모드 호환성 shim

- **BaseLib** — v3.x 가 안드로이드에서 게임을 검은 화면으로 크래시시켰습니다 (issues #8/#32/#55). 현재 빌드는 BaseLib 과 그 의존 모드들을 로드·실행하며, 한 가지 트레이드오프가 남아 있습니다: BaseLib 의 **async hook 시스템** (`AfterCardPlayed` 등) 은 그 상태머신 IL 수술이 Mono Android 에서 메모리를 손상시키기 때문에 모바일에서 의도적으로 no-op 처리됩니다. BaseLib 의 비-hook API 로 콘텐츠를 추가하는 모드는 정상 작동하고, async hook 에 의존하는 모드는 로드는 되지만 해당 트리거가 발화하지 않습니다.
- **안드로이드에서 `release_info.json` 로드 (issue #9)** — 메인 메뉴 빌드 라벨 `????` 표시와 LAN 버전 불일치 핸드셰이크 실패를 수정.
- **LAN 멀티플레이 수정** — 4인 동시접속 (issue #18), 영속 클라이언트 netId (issue #26), 클라우드 "좀비 런" 정리 (issue #31).

### 별도 앱 아이덴티티

패키지 id 는 `com.game.sts2launcher.modmanager`, 외부 저장공간은 `/storage/emulated/0/StS2LauncherMM/` — 업스트림 APK 와 데이터를 공유하지 않고 나란히 설치할 수 있으며, 앱 라벨은 **"StS2 Launcher Mod"** 입니다.

## 기능

- **Steam 인증** — SteamKit2 로그인 + Steam Guard 2FA; 리프레시 토큰은 Android Keystore (AES-256-GCM, 하드웨어 TEE) 로 암호화 저장.
- **게임 다운로드** — Steam depot 다운로드 + 업데이트 확인 + 브랜치 선택.
- **Steam 창작마당** — 인앱 검색·구독·자동 다운로드/업데이트·모드 보관.
- **클라우드 세이브** — SteamKit2 CCloud API 기반 전체 Steam Cloud 동기화 + 명시적 충돌 해결 UI + 파괴적 쓰기 가드.
- **로컬 백업** — 수동/자동 세이브 전체 트리 스냅샷.
- **런처 자체 업데이트** — 인앱 업데이트 확인 + APK 설치 흐름.
- **모바일 적응** — 터치 입력, UI 스케일링, 폴더블/회전 레이아웃, 앱 라이프사이클 처리 (Harmony 런타임 패치).
- **LAN 멀티플레이** — UDP 브로드캐스트 탐색 + 수동 IP 접속 ([LAN 멀티플레이](#lan-멀티플레이) 참조).
- **셰이더 워밍업** — Vulkan 파이프라인 캐시 영속화 + 캔버스 우버셰이더로 첫 조우 프레임 끊김 제거.

## 모드 설치

**창작마당 (권장)** — 런치 화면에서 **MOD MANAGER** 탭 → WORKSHOP 탭에서 검색·구독. 다운로드·설치·업데이트는 자동입니다. 자세한 내용은 [사용설명서](docs/USER_GUIDE.md#12-모드-설치-선택) 참조.

**수동 설치** — 창작마당에 없는 모드는 파일 매니저로 `/storage/emulated/0/StS2LauncherMM/Mods/` 아래에 모드별 폴더로 넣습니다. 유효한 모드 폴더는 루트에 `.dll`, 선택적 `.pck`, `<ModId>.json` 매니페스트를 포함합니다 — PC 의 `Steam\steamapps\common\Slay the Spire 2\mods\` 와 동일한 구조입니다.

![Mods 폴더 구조](docs/images/mods_folder.jpg)

게임을 실행하고 PLAY → 게임 내장 "Load mods?" 다이얼로그를 수락하면 런처를 통해 한 번 재시작한 뒤 모드가 로드됩니다. 모드가 안 보이면 Debug 토글을 켜고 로그에서 `[Mods]` 라인을 확인하세요.

### 알려진 비호환

**Steamworks.NET** 을 직접 import 하는 모드 (예: QuickReload) 는 PLAY 시 크래시합니다 — Valve 가 안드로이드용 Steamworks SDK 네이티브 라이브러리를 제공하지 않고, 런처의 스텁 `libsteam_api.so` 는 링커만 만족시킵니다. 여러 Steamworks API 표면 (SteamID, 인증 티켓, 업적/스탯, 클라우드, UGC, 리더보드) 은 원칙적으로 SteamKit2 로 브리지 가능하며 모드별 shim 을 검토할 수 있으니, 특정 모드가 필요하면 이슈로 제보해 주세요. 진짜로 브리지 불가능한 것: SteamNetworkingSockets P2P, Steam 오버레이, SteamInput.

## 릴리즈 이력

버전별 변경 내역 (기술 노트 포함) 은 [Releases 페이지](https://github.com/iunius612/StS2-Launcher_Mod_Manager/releases)에 있습니다. 이 README 에 있던 0.2.x–0.3.11 포크 변경 상세는 [git 히스토리](https://github.com/iunius612/StS2-Launcher_Mod_Manager/blob/v0.3.23/README.md)에 보존되어 있습니다.

## 개발

### 동작 원리

시작 시 `STS2Mobile.dll` 이 `coreclr_create_delegate` 로 로드되어 [Harmony](https://github.com/pardeike/Harmony) 패치를 적용해 데스크톱 게임을 모바일에 적응시킵니다. 런처는 `GameStartupWrapper()` 를 가로채 게임 시작 전에 Steam 로그인 화면을 표시합니다.

- **런처 전용 모드** — 게임 파일이 없으면 최소 `bootstrap.pck` 가 Steam 로그인·게임 다운로드용 런처 UI 를 제공합니다.
- **일반 모드** — 게임 파일이 있으면 모든 패치가 `sts2.dll` 에 적용되고 인증 후 게임이 네이티브로 실행됩니다.

### 엔진 패치

안드로이드 특화 이슈를 위한 Godot 4.5.1 엔진 소스 커스텀 패치:

- **Vulkan 파이프라인 캐시 영속화** — 앱이 포커스를 잃을 때 컴파일된 파이프라인을 저장해, 안드로이드가 프로세스를 죽인 뒤의 재컴파일을 방지.
- **캔버스 우버셰이더** — 2D 렌더링 우버셰이더 폴백으로 블로킹 파이프라인 컴파일로 인한 첫 조우 VFX 끊김 제거.

### 프로젝트 구조

```
src/STS2Mobile/
  ModEntry.cs              # 진입점 ([UnmanagedCallersOnly] Apply())
  PatchHelper.cs           # 공용 패치 유틸 + 로깅
  Patches/                 # Harmony 패치 (관심사별 파일)
  Launcher/                # 프로그래매틱 Godot UI (MVC), Mod Hub / 창작마당 포함
  Steam/                   # SteamKit2 로그인, depot 다운로드, 클라우드 세이브, 창작마당
src/stubs/                 # 네이티브 라이브러리 스텁 (Steam API, Sentry)
android/                   # Godot 안드로이드 gradle 프로젝트
  src/.../GodotApp.java    # Activity, 어셈블리 셋업, Keystore 암호화
  assets/bootstrap.pck     # 런처 전용 모드용 최소 PCK
tools/memberref-audit/     # 새 게임 빌드 대비 패치 IL 정적 감사
scripts/                   # 빌드·툴링 스크립트
docs/                      # 사용설명서 + 이미지
```

### 사전 요구사항

- .NET 9 SDK
- Android SDK + NDK (버전은 `android/config.gradle` 참조)
- Python 3 (`make-bootstrap-pck.py` 및 SCons 용)
- `upstream/godot-export/` 에 원본 게임 파일
- 커스텀 Godot 엔진 빌드 (`scripts/build-godot.sh` 참조)
- `vendor/fmod-sdk/` 에 FMOD SDK

### 빌드

> **참고**: 이 저장소에 없는 바이너리들이 빌드에 필요합니다. 커스텀 Godot 엔진은 [godotengine/godot](https://github.com/godotengine/godot), Harmony 는 [Ekyso/Harmony](https://github.com/Ekyso/Harmony) (.NET 9 컴파일) 기반 — 엔진 상세는 업스트림 프로젝트를 참조하세요. FMOD 와 Spine 은 라이선스 문제로 재배포할 수 없습니다. [THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md) 참조.

```bash
bash scripts/build.sh
```

전체 파이프라인: 패처 `dotnet publish` → 산출 DLL 을 `android/assets/dotnet_bcl/` 로 복사 → 안드로이드 crypto 네이티브 lib 을 JNI libs 로 복사 → `gradle.properties` 버전 bump → `./gradlew assembleMonoRelease` 로 APK 빌드.

산출물: `android/build/outputs/apk/mono/release/StS2Launcher-v<version>.apk`

```bash
# 디바이스 설치
adb install -r android/build/outputs/apk/mono/release/StS2Launcher-v*.apk

# 클린 설치 (저장된 자격증명 + 캐시된 어셈블리 삭제)
adb shell pm clear com.game.sts2launcher.modmanager

# bootstrap PCK 재생성 (project.godot 변경 시에만)
python3 scripts/make-bootstrap-pck.py

# Godot 엔진 재빌드 (엔진 소스 변경 시에만)
bash scripts/build-godot.sh

# 네이티브 스텁 재빌드 (Android NDK 필요)
bash src/stubs/build_stubs.sh
```

### 기술 노트

- 네이티브 라이브러리 스텁 (`src/stubs/`) 은 데스크톱 전용 라이브러리 (Steamworks SDK, Sentry) 용 no-op `.so` 를 제공해 런타임 링커를 만족시킵니다.
- bootstrap PCK 는 게임 파일 없이 .NET 모듈 초기화를 가능하게 하는 최소 `project.godot` 래퍼입니다.
- 게임의 Sentry 플러그인은 `android.arm64` 빌드가 없어 PCK 패칭 + Harmony 패치로 비활성화됩니다.
- Godot SDK 소스 제너레이터를 쓸 수 없어 GodotSharp interop 은 `ModEntry.cs` 에서 수동 부트스트랩됩니다.

## LAN 멀티플레이

두 기기가 같은 로컬 네트워크에 있어야 합니다. 모바일 앱은 UDP 브로드캐스트로 주변 게임을 찾거나 PC 의 IP 를 직접 입력할 수 있습니다.

PC 쪽에서 Steam 실행 옵션에 `--fastmp` 를 추가하세요: **Steam > Slay the Spire 2 > 속성 > 실행 옵션** → `--fastmp`. 모바일 클라이언트가 기대하는 fast multiplayer 모드가 활성화됩니다.

PC 와 모바일은 **같은 Steam 브랜치**를 유지하세요 — 한 브랜치에서 올라간 클라우드 세이브는 다른 브랜치 클라이언트가 읽지 못하고, Steam 은 자동 복구 없는 일반 동기화 충돌만 표시합니다.

## 라이선스

이 프로젝트는 [MIT License](LICENSE) 를 따릅니다. 서드파티 의존성 라이선스는 [THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md) 참조. FMOD 는 수익이 발생하는 프로젝트에서 상용 라이선스가 필요하고, Spine Runtimes 는 유효한 Spine Editor 라이선스가 필요합니다.
