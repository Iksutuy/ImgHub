<h1 align="center">
  <img src="src/ImgHub.App/Assets/app-icon.png" width="128" height="128" alt="ImgHub" />
</h1>

<p align="center">
  <a href="README.md">English</a> · <a href="README.zh.md">简体中文</a> · <a href="README.ja.md">日本語</a> · <b>한국어</b>
</p>

<div align="center">

![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Android-blue)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
![Avalonia](https://img.shields.io/badge/Avalonia-12.1.2-8B44AC)
![Version](https://img.shields.io/badge/version-0.5.43-blue)
![License](https://img.shields.io/badge/license-GPL--3.0-blue)
[![CI](https://github.com/Iksutuy/ImgHub/actions/workflows/ci.yml/badge.svg)](https://github.com/Iksutuy/ImgHub/actions/workflows/ci.yml)

</div>

<p align="center">
  <b>순수 Vibe Coding 산출물입니다. 데스크톱은 사용 가능하고, Android는 아직 미완성입니다.</b>
</p>

# 🖼️ ImgHub

ImgHub는 "요청 본문을 손으로 짜는 건 사양하겠다"는 사람을 위한 이미지 생성 워크벤치입니다. 다섯 개 provider 중 아무거나 API 키를 넣으면 생성·편집·마스크·반복 작업을 창 하나에서 끝낼 수 있습니다. 그동안 비용 표가 계속 돌아가므로 이번 세션에 얼마를 썼는지 항상 알 수 있습니다.

겨냥하는 것은 가장 일상적인 루프입니다. 프롬프트를 쓰고, 결과를 보고, 잘못된 부분만 고치고, 마음에 드는 한 장을 남깁니다. 이 루프에 필요한 것은 모두 앱 안에 있으므로 파일 이름을 고치거나, 지출을 세거나, 요청 본문을 직접 조립하러 밖으로 나갈 필요가 없습니다.

# 🌠 스크린샷

<img src="assets/screenshots/main-window.png" alt="ImgHub 메인 창" width="100%">

**메인 창** —— 왼쪽은 생성 파라미터, 가운데는 미리보기와 마크업 도구 모음, 오른쪽은 히스토리와 provider별 누적 비용입니다. 상태 표시줄은 자체 점검 결과를 보여주고, 메시지 패널은 각 단계를 그때그때 기록합니다.

<img src="assets/screenshots/mask-edit.png" alt="마스크 부분 재생성" width="100%">

**마스크 부분 재생성** —— 사각형(또는 마커/브러시)으로 영역을 그린 뒤 알파가 있는 마스크로 전송합니다. 로그에 전체 흐름이 남습니다: 주석 저장 → 알파 포함 마스크 내보내기 → 참조 이미지 업로드 → 마스크 업로드 → 작업 제출.

<img src="assets/screenshots/prompt-guide.png" alt="프롬프트 설계 가이드" width="100%">

**프롬프트 설계 가이드** —— 프롬프트 작성법을 담은 내장 참고 자료입니다. 공통 개요에 더해 provider별 절(OpenAI GPT Image / Qwen Image)이 있으며, 둘이 선호하는 표현 방식이 다르기 때문입니다.

<img src="assets/screenshots/advanced-params.png" alt="고급 파라미터" width="100%">

**고급 파라미터** —— 공식 문서에 맞춘 파라미터 집합입니다: 배경, 압축률, 부분 이미지 스트리밍(SSE) 등이며, 각 항목은 선택한 모델이 실제로 받아들이는 범위로 자동 축소됩니다.

# 🌟 주요 기능

1. **생성**

   - 텍스트에서 이미지 생성. 다루는 모델 폭이 넓음 (OpenAI, Google, Seedream, Flux, Grok 등)
   - 배치 생성, 한 번에 1~10장. 상한은 모델과 provider별로 좁혀지고, Jimeng은 장수를 스스로 정합니다
   - 오프라인 모드: 결정적 플레이스홀더 렌더러로, 비용 없이 전체 파이프라인을 돌려볼 수 있음

2. **참조 이미지와 편집**

   - 요청당 최대 16장의 참조 이미지 (Qwen DashScope는 3장까지)
   - 편집 대상 이미지는 항상 1번 자리에 고정 — 모델이 참조 이미지를 위치로 해석하기 때문
   - 마스크 부분 재생성: 브러시, 마커, 사각형, 타원으로 영역을 그리면 `alpha=0`이 변경할 곳을 나타내는 알파 마스크를 내보냅니다

3. **Provider**

   - OpenRouter — 동기, SSE 부분 이미지 스트리밍 지원
   - APIMart — 비동기 폴링
   - OpenAI 공식
   - Qwen DashScope — 동기와 비동기 두 경로
   - Jimeng(Volcengine) — AK/SK 서명
   - 기업용 게이트웨이나 DashScope 전용 도메인을 위한 커스텀 엔드포인트 지원

4. **워크플로**

   - 프롬프트 다듬기: LLM이 후보 4개를 팝업으로 제시, 하나 선택
   - 히스토리와 provider별·합계 비용 관리, 실행 전 추정치 제공
   - 영역 마크업 캔버스: 확대, 이동, 실행 취소/다시 실행, 이미지별 주석 히스토리
   - 중단 복구: task_id가 저장되므로 예기치 않게 종료되기 전에 제출한 생성도 되찾을 수 있습니다

5. **인터페이스**

   - 중국어·영어·일본어, 설정에서 전환하며 즉시 반영
   - 라이트/다크 테마
   - 하나의 공유 UI가 넓은 화면에서는 3단, 좁은 화면에서는 세로 배치로 렌더링

# 🚀 시작하기

## 사전 요구 사항

Windows 10/11(x64). .NET 10 SDK는 소스에서 빌드할 때만 필요합니다.

## 다운로드

최신 빌드는 [Releases](https://github.com/Iksutuy/ImgHub/releases)에서:

| 플랫폼 | 파일 | 비고 |
|---|---|---|
| Windows 10/11 x64 | `imghub-<version>-win-x64.zip` | Native AOT, .NET 런타임 불필요. 압축을 풀고 바로 실행 |
| Android 7.0+ (API 23+) | `imghub-<version>-android.apk` | arm64-v8a + x86_64. 아직 미완성 |

Windows 패키지는 폴더째 압축을 풀어 주세요. `ImgHub.Desktop.exe`는 같은 폴더의 `libSkiaSharp.dll`, `libHarfBuzzSharp.dll`, `av_libglesv2.dll`을 불러옵니다. exe만 따로 꺼내면 곧바로 종료 코드 `0xC0000409`로 죽고 아무 메시지도 나오지 않습니다.

각 릴리스에는 `SHA256SUMS.txt`도 함께 들어 있습니다.

## 설정

오른쪽 아래 **⚙ 설정**을 열고 provider를 고른 뒤 API 키를 붙여 넣고 저장합니다.

환경 변수로도 설정할 수 있습니다:

```powershell
$env:OPENROUTER_API_KEY = "sk-or-v1-..."
$env:IMGHUB_APIMART_API_KEY = "sk-..."
```

## 비용 없이 먼저 써 보기

오프라인 모드는 결정적 플레이스홀더 이미지를 렌더링하며, 저장소·히스토리·실행 취소·영속화까지 포함한 전체 파이프라인을 API에 전혀 닿지 않고 돌립니다. 디버그 플래그 뒤에 숨어 있습니다. 실행 전에 `IMGHUB_DEBUG=1`을 설정하면 설정 오버레이에 **오프라인** 체크박스가 나타납니다. 체크하고 프롬프트를 입력한 뒤 **생성**을 누르세요.

데이터는 기본적으로 `%LOCALAPPDATA%\imghub`에 놓이고, `IMGHUB_HOME`으로 바꿀 수 있습니다. API 키도 이 폴더에 저장되며 애플리케이션 로그에서는 마스킹됩니다. 이름을 바꾸기 전의 폴더(`%LOCALAPPDATA%\imgagent`)가 이미 있으면 ImgHub는 그것을 계속 사용하므로 기존 이미지와 키가 그대로 남습니다.

# 🧩 지원 Provider

| Provider | 방식 | 비고 |
|---|---|---|
| OpenRouter | 동기 | SSE 부분 이미지 스트리밍 |
| APIMart | 비동기 | `mask_url`을 통한 실제 마스크 지원 |
| OpenAI 공식 | 동기 | 엔드포인트 2개. 모델마다 파라미터가 다름 |
| Qwen DashScope | 동기 + 비동기 | 어느 경로인지는 모델에 따라 다름 |
| Jimeng(Volcengine) | 비동기 | AK/SK 서명 |

요청은 각 사 문서에 파라미터 단위로 맞춰져 있습니다 — `background`, `output_compression`, `moderation`, 픽셀 단위 `size`, `seed`, 그리고 provider 라우팅(`only` / `order` / `ignore` / `sort` / `allow_fallbacks`). 각 사의 계약은 [docs/](docs/)에 있습니다.

마스크를 받을 수 없는 provider는 "원본 이미지 + 주석 합성" 경로로 물러나며, 마스크를 보낸 척하지 않고 UI에서 그 사실을 명시합니다.

# 🌈 UI 언어

UI에는 **중국어·영어·일본어**가 포함되어 있습니다. 설정 오버레이 하단에서 언어를 바꾸면 즉시 반영되고 재시작이 필요 없습니다. 설정은 `config.json`에 저장됩니다.

# 🏗️ 아키텍처

```
ImgHub.Core       플랫폼 비의존 비즈니스 계층 (UI 의존 없음)
    ↑
ImgHub.App        공유 UI (XAML + MVVM), 두 head에서 재사용
    ↑
ImgHub.Desktop / ImgHub.Android       얇은 플랫폼 head
```

`ImgHub.Core`는 Avalonia도 Android도 참조하지 않습니다. UI가 필요로 하는 모든 것은 순수한 데이터와 서비스로 표현되어 있으며, 그래서 단일 XAML 트리가 Windows와 Android를 함께 구동할 수 있고 테스트에 UI 배선이 필요 없습니다.

# 🛠️ 소스에서 빌드

```powershell
git clone https://github.com/Iksutuy/ImgHub.git
cd ImgHub

# 테스트: 449개, 전부 오프라인 — 네트워크 불필요, API 비용 없음
dotnet test tests\ImgHub.Core.Tests           # 236
dotnet test tests\ImgHub.Integration.Tests    # 213

# 데스크톱 실행
dotnet run --project src\ImgHub.Desktop

# 릴리스 패키지 생성 (AOT 데스크톱 zip + APK + SHA256SUMS.txt)
powershell -File tools\make-release.ps1
```

Android head 빌드에는 `dotnet workload install android`와 함께 Android SDK, JDK가 필요합니다. 패키징 전체 안내는 [docs/DELIVERY.md](docs/DELIVERY.md)에 있습니다.

# 📁 프로젝트 구조

```
ImgHub/
├── src/
│   ├── ImgHub.Core/                플랫폼 비의존 비즈니스 계층
│   ├── ImgHub.App/                 공유 UI (XAML + MVVM)
│   ├── ImgHub.Desktop/             Windows / Linux / macOS head
│   └── ImgHub.Android/             Android head
├── tests/
│   ├── ImgHub.Core.Tests/          단위 테스트 236개
│   └── ImgHub.Integration.Tests/   엔드투엔드 테스트 213개
├── legacy/                         이전 세대 Python + curses 구현
├── docs/                           문서
├── tools/                          릴리스 패키징, 아이콘·폰트 생성
├── build.ps1                       원샷 빌드 (데스크톱 + APK)
└── ImgHub.slnx                     솔루션 파일
```

`legacy/`는 더 이전의 Python 터미널 클라이언트로, C# 재작성의 참조 구현이자 동작 계약으로 남겨 두었습니다. 959개의 Python 테스트가 기대되는 API 페이로드, 비용 계산, 오류 처리를 지금도 고정하고 있습니다.

# 📖 문서

| 문서 | 내용 |
|---|---|
| [docs/HANDOVER.md](docs/HANDOVER.md) | 여기서 시작 — 실행 방법, 어디를 고치는지, 디버깅 |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | 계층 구조, 데이터 흐름, provider 차이, 확장 지점 |
| [docs/CONSTRAINTS.md](docs/CONSTRAINTS.md) | 하드 제약과 그 배경에 있는 함정 |
| [docs/DELIVERY.md](docs/DELIVERY.md) | 패키징과 릴리스 |
| [docs/FEATURES.md](docs/FEATURES.md) | 전체 기능 목록과 알려진 공백 |
| [docs/i18n.md](docs/i18n.md) | 다국어 지원 구현 방식 |
| [docs/port-status.md](docs/port-status.md) | 마이그레이션 진행 상황과 라운드별 수정 기록 |
| [CHANGELOG.md](CHANGELOG.md) | 버전 변경 이력 |
| [CONTRIBUTING.md](CONTRIBUTING.md) | 기여 가이드 |
| [SECURITY.md](SECURITY.md) | 보안 모델과 비공개 제보 창구 |
| [NOTICE.md](NOTICE.md) | 서드파티 라이선스 (내장 폰트 포함) |

# 📝 로드맵

알려진 공백을 대략 우선순위 순으로:

- **Android 마무리** — APK는 빌드되고 동작하지만 중국어 렌더링은 실기기에서 검증되지 않았고, "갤러리에 저장"은 MediaStore에 쓰는 대신 시스템 파일 선택기를 거칩니다.
- **플랫폼 범위** — 검증된 것은 Windows뿐입니다. Avalonia 자체는 크로스 플랫폼이라 Linux와 macOS도 동작해야 하지만 확인하지 않았습니다.
- **환경 진단** — Python 버전의 `doctor.py` 패널은 이식되지 않았습니다.
- **키보드 단축키** — 메시지 패널의 단축키 안내는 텍스트일 뿐이고 실제 키 할당은 없습니다.
- **파일 크기** — `ImageApi.cs`와 `MainViewModel.cs`가 커졌습니다. 분리는 별도 리팩터링으로 계획하고 있습니다.

# 🤝 기여하기

이슈와 PR 모두 환영합니다. [CONTRIBUTING.md](CONTRIBUTING.md)에서 가장 중요한 세 가지 제약을 다룹니다. 계층 분리 규칙, 레이아웃이 두 벌인 상황에서 UI를 어디서 고칠지, 그리고 컴파일 바인딩입니다.

미리 알아 두면 좋은 두 가지:

- 테스트는 전부 결정적 플레이스홀더 이미지로 오프라인에서 돌기 때문에 실행해도 돈이 들지 않습니다.
- XAML 변경은 테스트로 커버되지 않습니다. 뷰를 건드렸다면 데스크톱 앱을 실행해 한 번 눌러 보세요.

# 📜 라이선스

GPL-3.0 — [LICENSE](LICENSE) 참조.

내장된 서드파티 구성 요소와 폰트는 [NOTICE.md](NOTICE.md)에 나열되어 있습니다. 프로젝트에 API 자격 증명은 포함되지 않습니다. 모든 키는 사용자가 직접 제공하고 로컬에 저장됩니다.
