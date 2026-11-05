**한국어** | [English](README.en.md)

# ShareCom

같은 와이파이에 있는 Windows PC 2~4대를 마우스·키보드 하나로 함께 조작하고, 클립보드를 공유하고, 파일을 옮기는 프로그램이다.

- 내려받기: [최신 릴리즈](https://github.com/v0o0v/shareCom/releases/latest) (`ShareCom.exe` 파일 하나, 설치 필요 없음)
- 설계: [docs/design.md](docs/design.md)
- 실기 확인 목록: [docs/manual-test.md](docs/manual-test.md)

## 지금 되는 것

- 같은 망의 PC 자동 검색, 6자리 코드로 처음 한 번 짝 맺기
- TLS 암호화(기본 켬, [설정…]에서 끌 수 있음)
- 화면 가장자리로 밀어 다른 PC로 넘어가기, 키보드 함께 넘어감
- 최대 4대, PC마다 모니터 여러 대. 트레이 메뉴 [화면 배치…]에서 PC를 끌어 실제 책상처럼 놓으면(상하좌우, 배율 차이 반영) 모든 PC에 같은 배치가 퍼진다. 원격 PC에서 또 다른 PC로 바로 넘어간다.
- 자동 소개: 새 PC는 기존 PC 하나와만 짝을 맺으면 나머지 PC와도 짝이 된다. 짝 풀기도 모든 PC로 퍼진다.
- 단축키: `Scroll Lock` 넘기기 잠금, `Ctrl+Alt+1~4` PC 바로 이동, `Ctrl+Alt+Esc` 긴급 복귀(앞의 둘은 [설정…]에서 바꿀 수 있음)
- 끊기면 곧바로 내 PC로 복귀, 상대가 꺼졌다 켜지면 자동 재연결
- 클립보드 자동 공유: 글자, 서식 있는 글(HTML·RTF), 이미지(PNG). 크기 제한 없음, 큰 내용을 보내는 중에도 마우스·키보드가 먼저 간다. 비밀번호 관리자가 공유 제외 표시를 붙인 내용은 보내지 않는다.
- 파일 옮기기: 한 PC 탐색기에서 복사하고 다른 PC 탐색기에서 붙여넣으면, 붙여넣는 순간 원본 PC에서 받아온다(폴더 통째로, 크기 제한 없음). 트레이 메뉴 [파일 보내기]로 골라 보내면 받는 쪽이 [받기]/[거절]을 고르고, `다운로드\shareCom` 에 저장된다(같은 이름은 `이름 (2)`).
- 로그인할 때 자동 시작: 작업 스케줄러에 '가장 높은 권한'으로 등록해 UAC 창 없이 켠다. 처음 실행할 때 한 번 묻고, 트레이 메뉴 [설정…]에서 켜고 끈다.
- 설정 창: 자동 시작, 클립보드 공유, 암호화, 넘기기 잠금, 단축키, 자동 업데이트, 이 PC 정보.
- 자동 업데이트(v0.6.0부터): GitHub 정식 릴리즈를 6시간마다 확인해 서명을 확인하고 받은 뒤, 이 PC를 5분 넘게 쓰지 않을 때 스스로 바꾼다. 새 버전이 제대로 켜지지 않으면 이전 버전으로 되돌린다. [설정…]에서 끄거나 [지금 확인]을 누를 수 있다. v0.5.x 는 v0.6.0 으로 한 번 손으로 바꿔야 한다.

## 빌드

.NET 10 SDK 가 필요하다.

```bash
dotnet test
```

```bash
dotnet publish src/ShareCom.App -c Release -o publish
```

`publish\ShareCom.exe` 하나를 각 PC에 복사해 실행한다(런타임 포함, 설치 필요 없음). 관리자 권한으로 실행되고, 첫 실행 때 방화벽 규칙을 추가한다.

개발 중에 UAC 창 없이 띄워 보려면 `-p:NoAdmin=true` 로 빌드한다. 이 경우 관리자 권한 창 조작과 방화벽 규칙 추가는 되지 않는다.

## 구조

| 경로 | 내용 |
|---|---|
| `src/ShareCom.Core` | 프로토콜, 연결·짝 맺기·재연결(`Node`), 입력 라우팅(`InputRouter`), 배치 계산·동기화(`DeskLayout`, `LayoutSync`), 자동 소개(`TrustSync`), 클립보드 동기화, 파일 전송(`FileTransferService`). Windows API 를 쓰지 않는다. |
| `src/ShareCom.App` | 트레이 UI, 배치 편집기(`LayoutEditorForm`), 저수준 입력 훅, `SendInput` 재현, 클립보드 감시, 탐색기용 가상 파일(`VirtualFileDataObject`), 설정 저장, 방화벽 |
| `tests/ShareCom.Core.Tests` | 자동 시험 |
| `tools/ShareCom.ReleaseTool` | 업데이트 서명 키 만들기·서명·검증·백업 |
| `scripts/release.ps1` | 시험·빌드·서명·GitHub 릴리즈를 한 번에 |

설정과 기록은 `%APPDATA%\ShareCom\` 에 있다.
