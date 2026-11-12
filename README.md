# Server Monitor

여러 PC·서버의 CPU/메모리 상태를 수집해 실시간으로 보여주고, 임계치를 넘으면 알람을 남기는 모니터링 플랫폼입니다.
에이전트가 지표를 수집해 서버로 보내고, 서버는 DB에 저장하면서 SignalR로 관제 화면에 즉시 밀어줍니다.

![dashboard](docs/dashboard.png)

## 주요 기능

- **에이전트**: 1초 주기로 CPU/메모리를 측정해 서버로 전송. 시뮬레이션 모드(`sim`) 지원
- **네트워크 장애 대응**: 서버가 꺼져 있어도 1초 수집은 계속되고, 메모리 큐에 쌓았다가 복구되면 오래된 순서대로 재전송
- **API 키 인증**: 지표 수집 API(`POST /api/metrics`)는 `X-Api-Key` 헤더가 없거나 틀리면 401
- **실시간 푸시**: 폴링 대신 SignalR로 지표와 알람을 관제 화면에 즉시 전달
- **자동 재연결**: 서버가 재시작되면 관제 화면이 스스로 다시 연결하고 현재 상태를 다시 불러옴
- **알람**: CPU 80% 이상이면 알람 기록 (장비별 60초 쿨다운으로 중복 방지)
- **WPF 관제 화면**: 장비 목록(정상/경고/오프라인), 선택 장비의 최근 60초 그래프, 최근 알람 이력

## 구조

```
Agent (수집) --HTTP POST + API Key--> Server (ASP.NET Core + SQLite) --SignalR push--> Dashboard (WPF)
```

```
src/
  Agent/        지표 수집, 로컬 큐 버퍼링, 재전송
  Server/       Minimal API, SignalR Hub, SQLite 저장, 알람 판정
  Dashboard/    WPF 관제 화면 (MVVM)
    Models/       서버와 주고받는 데이터 형식
    Services/     MonitorClient: REST 조회 + SignalR 수신 + 자동 재연결
    ViewModels/   MainViewModel, DeviceRowViewModel: 화면 상태와 로직
    MainWindow    화면 모양과 그래프 그리기
```

## 기술 스택

C# / .NET 10, ASP.NET Core Minimal API, SignalR, SQLite (Microsoft.Data.Sqlite), WPF (MVVM)

## 실행 방법

요구 사항: Windows, .NET 10 SDK (CPU/메모리 실측은 Windows 성능 카운터를 사용하므로 Windows 전용)

```powershell
dotnet build

# (선택) API 키 지정. 서버와 에이전트가 같은 값을 써야 합니다. 기본값은 dev-secret-key
$env:MONITOR_API_KEY = "my-secret"

# 1) 서버
dotnet run --no-build --project src/Server --urls http://localhost:5000

# 2) 에이전트 (실제 측정은 장비 이름만, 시뮬레이션은 sim 추가)
dotnet run --no-build --project src/Agent -- pc-01 sim
dotnet run --no-build --project src/Agent -- pc-02 sim

# 3) 관제 화면
dotnet run --no-build --project src/Dashboard
```

서버, 에이전트, 관제 화면은 각각 별도 터미널에서 실행합니다. 서버를 껐다 켜도 에이전트는 수집을 이어가고, 관제 화면은 자동으로 다시 연결됩니다.

## API

| 메서드 | 경로 | 설명 |
|---|---|---|
| POST | /api/metrics | 에이전트가 지표 전송 (`X-Api-Key` 필요) |
| GET | /api/metrics?limit=10 | 최근 지표 조회 |
| GET | /api/devices | 장비별 최신 상태 |
| GET | /api/devices/{name}/history?limit=60 | 장비별 최근 기록 (그래프용) |
| GET | /api/alerts?limit=20 | 알람 이력 |
| WS | /hub/monitor | SignalR 허브. 이벤트: `MetricReceived`, `AlertRaised` |

## 설계 메모와 한계

- 에이전트 큐는 메모리에만 있어서, 서버가 꺼진 상태에서 에이전트까지 종료하면 대기 중이던 지표는 사라집니다.
- GET API와 허브는 인증이 없습니다. 내부망 사용을 가정한 구성입니다.
- 알람 임계치(80%)와 쿨다운(60초)은 서버 코드의 상수입니다.

## 앞으로의 계획

- [ ] 에이전트 버퍼를 디스크에 저장 (에이전트 재시작에도 보존)
- [ ] 장비별 등록 토큰 인증
- [ ] Docker Compose 배포, 부하 테스트 수치 측정
- [ ] AI 기반 이상 탐지
- [ ] 웹 대시보드
