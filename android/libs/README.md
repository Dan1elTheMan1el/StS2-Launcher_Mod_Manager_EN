# android/libs — 네이티브 프리빌트 출처와 재빌드 방법

이 디렉터리의 `*.so` / `*.jar` 는 `.gitignore` 로 git 에 추적되지 않는다. **어디서 왔는지 기록이 없으면 버전이 보이지 않게 낡는다** — 실제로 issue #78 이 그렇게 터졌다(FMOD 런타임이 게임보다 구버전이라 모드 뱅크를 못 읽고, 이벤트 재생 시 네이티브 널 역참조로 앱이 즉사). 새 바이너리를 넣거나 갱신하면 **이 표를 반드시 갱신하라.**

## 현재 구성 (arm64-v8a)

| 파일 | 출처 | 버전 | 비고 |
|------|------|------|------|
| `libfmod.so`, `libfmodstudio.so` | FMOD Studio API for Android (`req_files/fmodstudioapi20313android.tar.gz`) | **2.03.13** | 게임 PC 판은 FMOD 2.3.6. **게임과 같거나 더 최신이어야 한다** — 모드 저자들이 현행 FMOD Studio 로 구운 뱅크(FEV v146)를 읽으려면 2.03+ 필요 |
| `fmod.jar` | 위 SDK (`api/core/lib/fmod.jar`) | 2.03.13 | 실제 바이트는 upstream 것과 동일(버전 간 미변경) |
| `libGodotFmod.android.template_{release,debug}.arm64.so` | **직접 빌드** (아래 절차) | fmod-gdextension `6.1.0-4.5.0` + FMOD 2.03.13 헤더 + 로컬 패치 | 로컬 패치: ① `_create_event_instance` / `_play_one_shot` 널 가드(issue #78 즉사 원인) ② `add_bank` 실패 시 `FMOD_ErrorString` 출력 |
| `libspine_godot.android.template_*.arm64.so` | upstream(Ekyso) 프리빌트 그대로 | 불명(문자열 stripped) | ⚠️ **FMOD 와 같은 부류의 잠복 위험** — 게임 스켈레톤 데이터와 포맷 계약이 걸림. 현재는 정상 동작. 게임이 Spine 런타임을 올리면 같은 방식으로 깨진다 |
| `libmonosgen-2.0.so`, `libmono-component-*.so`, `libSystem.*.so` | Godot 4.5.1 mono export templates (`req_files/Godot_v4.5.1-stable_mono_export_templates.tpz`) | .NET 9 / Godot 4.5.1 | 엔진과 짝이 맞아야 함. .NET 10 등으로 올리려면 **엔진부터** 재빌드해야 한다 |
| `libsteam_api.so` | upstream 프리빌트 | — | 안드로이드에선 미사용(우리는 SteamKit2 로 Steam 을 붙인다). 사실상 잔재 |
| `libsentry.so` | upstream 프리빌트(3.7KB 스텁) | — | Sentry 의 Android GDExtension 은 아예 없음 → 부팅 시 `No GDExtension library found ... sentry.gdextension` 은 **알려진 무해 노이즈** |

## 빌드 전 체크 (다른 세션·워크트리 포함)

**FMOD 4종은 `D:/git/req_files/fmod-android-2.03.13/` 에 정식 사본이 있다. 재빌드 없이 그대로 복사하면 된다:**

```bash
SRC=/d/git/req_files/fmod-android-2.03.13
for v in release debug; do
  cp $SRC/libfmod.so $SRC/libfmodstudio.so android/libs/$v/arm64-v8a/
  cp $SRC/fmod.jar                          android/libs/$v/
  cp $SRC/libGodotFmod.android.template_release.arm64.so android/libs/$v/arm64-v8a/
  cp $SRC/libGodotFmod.android.template_release.arm64.so android/libs/$v/arm64-v8a/libGodotFmod.android.template_debug.arm64.so
done
```

빌드 전에 항상 확인:
```bash
md5sum android/libs/release/arm64-v8a/libfmod.so   # 678ca6c0f92d956c3b62e08b34634a0a (FMOD 2.03.13)
md5sum android/libs/release/arm64-v8a/libGodotFmod.android.template_release.arm64.so  # 19611f81685e8550a7bba1d6fa9e2f5f
```
다른 값이면 **구버전 FMOD 로 빌드하는 것**이다(issue #78 재현). 2026-07-12 시점에 구 FMOD(2.02) 바이너리는 이 PC 의 모든 워크트리·vanilla 클론·gradle 캐시에서 제거했다. 굳이 구 바이너리가 필요하면 `req_files/StS2Launcher-v0.2.0.apk` 의 `lib/arm64-v8a/` 에서 꺼낼 수 있다.

> FMOD SDK 자체는 `vendor/fmod-sdk/`(= 2.03.13, 헤더 포함)에 이미 벤더링되어 있다. **이 SDK 는 내내 repo 안에 있었는데 `android/libs/` 에는 upstream 프리빌트(2.02)가 들어가 있었다** — 그래서 아무도 버전 불일치를 눈치채지 못했다.

## libGodotFmod 재빌드 절차 (검증됨, 2026-07-12)

전제: Python 3, Android NDK(예: `~/AppData/Local/Android/Sdk/ndk/28.1.13356709`), FMOD Android SDK(`vendor/fmod-sdk/` 또는 `req_files/fmodstudioapi20313android.tar.gz`).

```bash
pip install scons
git clone https://github.com/utopia-rise/fmod-gdextension
cd fmod-gdextension
git checkout 6.1.0-4.5.0           # fmod_cache.cpp:50 / fmod_server.cpp:705 와 일치하는 태그
git submodule update --init --depth 1 godot-cpp    # godot-cpp 4.5-stable

# FMOD SDK 를 SConstruct 가 기대하는 레이아웃으로 배치
#   <fmod_lib_dir>/android/core/{inc,lib/arm64-v8a}
#   <fmod_lib_dir>/android/studio/{inc,lib/arm64-v8a}
tar xzf fmodstudioapi20313android.tar.gz
mkdir -p ../libs/fmod/android
cp -r fmodstudioapi20313android/api/core   ../libs/fmod/android/core
cp -r fmodstudioapi20313android/api/studio ../libs/fmod/android/studio

export ANDROID_NDK_ROOT=/c/Users/<user>/AppData/Local/Android/Sdk/ndk/28.1.13356709
python -m SCons platform=android target=template_release arch=arm64 fmod_lib_dir=../libs/fmod/ -j8
# -> demo/addons/fmod/libs/android/arm64/libGodotFmod.android.template_release.arm64.so
```

**주의 (실제로 걸렸던 함정):**

- SConstruct 의 android 분기가 `env.Append(LIBS=['libfmod.so', ...])` 로 파일명을 통째로 넘겨 링커가 `-lfmod.so` 를 찾다 실패한다. `LIBS=['fmod', 'fmodstudio']` 로 고쳐야 링크된다(로컬 패치).
- **런타임(.so)만 갈아끼우면 안 된다.** `libGodotFmod` 는 FMOD 헤더로 컴파일되므로, FMOD 버전을 올리면 GDExtension 도 **같은 SDK 헤더로 재빌드**해야 한다. 안 그러면 기기에서 `[FMOD ERROR] There is a version mismatch between the FMOD header and either the FMOD Studio library or FMOD Low Level library` 가 뜨고 오디오가 전멸한다(실측).
- 결과 `.so` 를 `android/libs/{release,debug}/arm64-v8a/` 양쪽에 넣고, 같은 SDK 의 `libfmod.so`/`libfmodstudio.so`/`fmod.jar` 도 함께 갱신한다.

## 검증 방법 (기기)

```
FMOD Sound System: Successfully initialized      <- 초기화 OK (version mismatch 없음)
[FmodBank] <Mod>/<bank>: SKIP (mod loaded it itself)   <- 모드 뱅크가 res:// 에서 바로 읽힘 = 정상
Cannot load bank ... (FMOD error 70: ...)        <- 우리 폴백이 중복 로드 시도 시(무해하나 없어야 정상)
```
게임 오디오(카드/타격음)와 모드 사운드가 실제로 들리는지까지 확인할 것.
