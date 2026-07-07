# memberref-audit

STS2Mobile.dll 이 게임 어셈블리(sts2.dll)로 갖는 **컴파일타임 참조(MemberRef) 전수를 대상 sts2.dll 과 대조**해, 게임 업데이트로 사라진/시그니처가 바뀐 메서드·필드를 빌드 없이 찾아낸다.

게임 베타가 시그니처를 바꾸면 (예: v0.108.0 의 save-path 헬퍼 `bool? forceModState` 추가) 구 시그니처로 컴파일된 IL 은 해당 호출을 포함한 메서드의 JIT 시점에 MissingMethodException 을 던진다. grep 은 소스의 명시적 호출만 찾지만, 이 도구는 산출물의 MemberRef 테이블을 직접 읽으므로 누락이 없다 (실제로 v0.108 대응 때 grep 이 3건, 이 도구가 4건째 `RunHistorySaveManager.GetHistoryPath` 를 발견).

## 사용법

```sh
cd tools/memberref-audit
dotnet build -c Release
dotnet bin/Release/net9.0/audit.dll <STS2Mobile.dll> <대상 sts2.dll> [scope=sts2]
```

- 종료코드 0 = 모든 참조 유효, 1 = MISSING 존재 (라인별 출력), 2 = 사용법 오류.
- 게임 업데이트 호환 확인: publish 산출물을 **구 참조본과 신 게임 DLL 양쪽** 에 대해 돌려 둘 다 0 missing 이면 듀얼 브랜치 안전.
- 신 게임 DLL 소스: PC Steam 설치본 `C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll`.

## 한계

- MemberRef(외부 참조)만 검사 — 리플렉션 문자열 조회(`GetMethod("...")`)는 못 본다.
- 시그니처 문자열 비교 기반이므로 제네릭 파라미터는 `!0`/`!!0` 표기로 정규화해 비교한다.
