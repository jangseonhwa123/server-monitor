# Server Monitor

여러 장비(PC/서버)의 CPU·메모리 상태를 수집해 저장하고, 실시간으로 확인하며, 임계값을 넘으면 알람을 남기는 경량 모니터링 플랫폼입니다. (Zabbix/Datadog의 축소판)

![관제 화면](docs/dashboard.png)

## 주요 기능

- **에이전트**: 장비에서 1초마다 CPU·메모리 사용률을 수집해 서버로 전송합니다. 서버 연결이 끊겨도 종료되지 않고 재시도합니다.
- **서버(ASP.NET Core)**: 수집 API, SQLite 저장, 장비별 최신 상태 조회, 장비별 최근 기록 조회, CPU 임계값 초과 알람 기록(장비당 60초 쿨다운)
- **관제 클라이언트(WPF)**: 장비 목록과 상태(정상/경고/오프라인), 선택한 장비의 CPU·메모리 실시간 그래프, 알람 이력
- **시뮬레이션 모드**: 가짜 장비를 여러 대처럼 실행해 알람과 다중 장비 동작을 검증

## 구조

```
[Agent: pc-01] --\
[Agent: pc-02] ----> [Server: ASP.NET Core + SQLite] <---- [Dashboard: WPF]
[Agent: 실제 PC] -/
```

## 기술 스택

C# / .NET 10, ASP.NET Core (Minimal API), SQLite (Microsoft.Data.Sqlite), WPF, Git

## 실행 방법

요구 사항: Windows, .NET 10 SDK

```powershell
git clone https://github.com/jangseonhwa123/server-monitor.git
cd server-monitor
dotnet build

# 1) 서버
dotnet run --no-build --project src/Server --urls http://localhost:5000

# 2) 에이전트 (실제 측정은 장비 이름만, 시뮬레이션은 sim 추가)
dotnet run --no-build --project src/Agent -- pc-01 sim
dotnet run --no-build --project src/Agent -- pc-02 sim

# 3) 관제 화면
dotnet run --no-build --project src/Dashboard
```

## API

| 메서드 | 경로 | 설명 |
|---|---|---|
| POST | /api/metrics | 에이전트가 지표 전송 |
| GET | /api/metrics?limit=10 | 최근 지표 조회 |
| GET | /api/devices | 장비별 최신 상태 |
| GET | /api/devices/{name}/history?limit=60 | 장비별 최근 기록 (그래프용) |
| GET | /api/alerts?limit=20 | 알람 이력 |

## 앞으로의 계획

- 서버 불가 시 에이전트 로컬 버퍼링 후 재전송
- 장비 등록 토큰 인증
- SignalR 기반 실시간 푸시 (현재는 1초 주기 조회)
- AI 기반 이상 탐지
- Docker Compose 배포, 부하 테스트 수치 측정
- 웹 대시보드
