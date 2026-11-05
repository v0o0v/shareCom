# ShareCom

같은 와이파이에 있는 Windows PC 2~4대를 마우스·키보드 하나로 함께 조작하고 클립보드를 공유하는 프로그램이다. 파일 옮기기는 다음 단계에서 넣는다.

- 설계: [docs/design.md](docs/design.md)
- 실기 확인 목록: [docs/manual-test.md](docs/manual-test.md)

## 지금 되는 것 (2단계까지)

- 같은 망의 PC 자동 검색, 6자리 코드로 처음 한 번 짝 맺기
- TLS 암호화(기본 켬, 트레이 메뉴에서 끌 수 있음)
- 화면 가장자리로 밀어 다른 PC로 넘어가기(좌우 배치), 키보드 함께 넘어감
- 단축키: `Scroll Lock` 넘기기 잠금, `Ctrl+Alt+1~4` PC 바로 이동, `Ctrl+Alt+Esc` 긴급 복귀
- 끊기면 곧바로 내 PC로 복귀, 상대가 꺼졌다 켜지면 자동 재연결
- 클립보드 자동 공유: 글자, 서식 있는 글(HTML·RTF), 이미지(PNG). 크기 제한 없음, 큰 내용을 보내는 중에도 마우스·키보드가 먼저 간다. 비밀번호 관리자가 공유 제외 표시를 붙인 내용은 보내지 않는다.

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
| `src/ShareCom.Core` | 프로토콜, 연결·짝 맺기·재연결(`Node`), 입력 라우팅(`InputRouter`). Windows API 를 쓰지 않는다. |
| `src/ShareCom.App` | 트레이 UI, 저수준 입력 훅, `SendInput` 재현, 설정 저장, 방화벽 |
| `tests/ShareCom.Core.Tests` | 자동 시험 |

설정과 기록은 `%APPDATA%\ShareCom\` 에 있다.
