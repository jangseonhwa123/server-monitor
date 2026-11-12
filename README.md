# Server Monitor

![CI](https://github.com/jangseonhwa123/server-monitor/actions/workflows/ci.yml/badge.svg)

여러 PC·서버의 CPU/메모리 상태를 수집해 실시간으로 보여주고, 임계치를 넘으면 알람을 남기는 모니터링 플랫폼입니다.
에이전트가 지표를 수집해 서버로 보내고, 서버는 MySQL에 저장하면서 SignalR로 관제 화면에 즉시 밀어줍니다.

![dashboard](docs/dashboard.png)

## 주요 기능

- **에이전트**: 1초 주기로 CPU/메모리를 측정해 서버로 전송. 시뮬레이션 모드(`sim`) 지원
- **네트워크 장애 대응**: 서버가 꺼져 있어도 1초 수집은 계속되고, 메모리 큐에 쌓였다가 복구되면 오래된 순서대로 재전송
- **API 키 인증**: 지표 수집 API(`POST /api/metrics`)는 `X-Api-Key` 헤더가 없거나 틀리면 401
- **입력 검증**: 장비 이름 누락/길이 초과, 0~100을 벗어나거나 비정상인 수치, 비정상 시각은 400으로 거절
- **실시간 푸시**: 폴링 대신 SignalR로 지표와 알람을 관제 화면에 즉시 전달
- **자동 재연결**: 서버가 재시작되면 관제 화면이 스스로 다시 연결하고 현재 상태를 다시 불러옴
- **알람**: CPU 80% 이상이면 알람 기록 (장비별 60초 쿨다운으로 중복 방지)
- **데이터 보존 기간**: 오래된 지표와 알람을 주기적으로 삭제 (기본 7일, 1만 건씩 나눠 삭제해 DB를 오래 잠그지 않음)
- **WPF 관제 화면**: 장비 목록(정상/경고/오프라인), 선택 장비의 최근 60초 그래프, 최근 알람 이력

## 구조

```
Agent (수집) --HTTP POST + API Key--> Server (ASP.NET Core + MySQL) --SignalR push--> Dashboard (WPF)
```

```
src/
  Agent/        지표 수집, 로컬 큐 버퍼링, 재전송
  Server/       Minimal API, SignalR Hub, MySQL 저장, 알람 판정, 쓰기 큐, 보존 기간 삭제
  Dashboard/    WPF 관제 화면 (MVVM)
    Models/       서버와 주고받는 데이터 형식
    Services/     MonitorClient: REST 조회 + SignalR 수신 + 자동 재연결
    ViewModels/   MainViewModel, DeviceRowViewModel: 화면 상태와 로직
    MainWindow    화면 모양과 그래프 그리기
  LoadTest/     가상 에이전트 N대로 부하를 만드는 측정 도구
tests/
  Server.Tests/ xUnit + Testcontainers(MySQL) 통합 테스트
```

## 기술 스택

C# / .NET 10, ASP.NET Core Minimal API, SignalR, MySQL 8.4 (MySqlConnector), WPF (MVVM), Docker Compose, xUnit + Testcontainers, GitHub Actions

## 실행 방법

요구 사항: .NET 10 SDK, Docker (DB용). 관제 화면(WPF)과 실제 CPU/메모리 측정은 Windows 성능 카운터를 쓰므로 Windows 전용입니다.

```powershell
dotnet build

# 0) DB (MySQL을 도커로 실행. 호스트 포트 3307)
docker compose up -d db

# (선택) API 키 지정. 서버와 에이전트가 같은 값을 써야 합니다. 기본값은 dev-secret-key
$env:MONITOR_API_KEY = "my-secret"

# 1) 서버
dotnet run --no-build --project src/Server --urls http://127.0.0.1:5000

# 2) 에이전트 (실제 측정은 장비 이름만, 시뮬레이션은 sim 추가)
dotnet run --no-build --project src/Agent -- pc-01 sim
dotnet run --no-build --project src/Agent -- pc-02 sim

# 3) 관제 화면
dotnet run --no-build --project src/Dashboard
```

서버, 에이전트, 관제 화면은 각각 별도 터미널에서 실행합니다. 서버를 껐다 켜도 에이전트는 수집을 이어가고, 관제 화면은 자동으로 다시 연결됩니다.

## Docker로 실행

```powershell
docker compose up -d --build
```

MySQL, 서버, 시뮬레이션 에이전트 2대(pc-01, pc-02)가 함께 뜹니다. DB가 준비된 뒤에 서버가 시작되도록 헬스체크로 순서를 맞췄고, 데이터는 볼륨(`mysql-data`)에 남습니다. 관제 화면은 윈도우에서 `dotnet run --no-build --project src/Dashboard`로 실행합니다. 컨테이너는 리눅스라 성능 카운터 실측은 불가능하고 시뮬레이션 모드만 동작합니다.

### 환경변수

| 이름 | 대상 | 설명 | 기본값 |
|---|---|---|---|
| `ConnectionStrings__Default` | 서버 | MySQL 연결 문자열 | `127.0.0.1:3307`의 monitor DB |
| `MONITOR_API_KEY` | 서버/에이전트 | 지표 수집 API 키 | `dev-secret-key` |
| `MONITOR_RETENTION_DAYS` | 서버 | 지표/알람 보존 기간(일) | `7` |
| `MONITOR_SERVER_URL` | 에이전트 | 지표를 보낼 서버 주소 | `http://127.0.0.1:5000/api/metrics` |

## 데이터 저장소

MySQL 8.4에 `Metrics`, `Alerts` 두 테이블을 서버 시작 시 자동 생성합니다(DB가 아직 준비 중이면 재시도).

- 조회 패턴에 맞춰 `Metrics(DeviceName, Id)`, `Metrics(Timestamp)`, `Alerts(DeviceName, Type, Timestamp)`, `Alerts(Timestamp)` 인덱스를 두었습니다.
- 시각은 `DATETIME(6)`(마이크로초)에 UTC로 저장하고, API는 끝에 `Z`가 붙은 UTC로 응답합니다.
- 장비 이름은 `utf8mb4_bin` 콜레이션(정렬 규칙)라 대소문자를 구분합니다(`PC-01`과 `pc-01`은 다른 장비).
- 지표는 큐에 모아 한 트랜잭션에서 여러 행을 한 번에 INSERT합니다(최대 500행/문장).

## API

| 메서드 | 경로 | 설명 |
|---|---|---|
| POST | /api/metrics | 에이전트가 지표 전송 (`X-Api-Key` 필요, 잘못된 값은 400, 큐가 가득 차면 503) |
| GET | /api/metrics?limit=10 | 최근 지표 조회 |
| GET | /api/devices | 장비별 최신 상태 |
| GET | /api/devices/{name}/history?limit=60 | 장비별 최근 기록 (그래프용) |
| GET | /api/alerts?limit=20 | 알람 이력 |
| WS | /hub/monitor | SignalR 허브. 이벤트: `MetricReceived`, `AlertRaised` |

`limit`은 1~1000으로 보정됩니다(범위를 벗어나도 에러 없이 처리).

## 테스트와 CI

xUnit 통합 테스트 26개가 Testcontainers로 실제 MySQL 컨테이너를 띄워 검증합니다. 인증, 입력 검증, 대소문자 구분, UTC 시각, limit 보정, 보존 기간 삭제(1만 건을 넘는 대량 삭제 포함)를 다룹니다.

GitHub Actions는 세 작업으로 구성됩니다.

- `test`: 리눅스에서 통합 테스트 실행
- `build-windows`: 윈도우에서 WPF 포함 전체 빌드
- `docker-smoke`: `docker compose up`으로 전체 스택을 띄우고 에이전트 데이터가 흐르는지 확인

## 성능 측정

부하 생성기(`src/LoadTest`)로 가상 에이전트 N대가 1초에 한 건씩 `POST /api/metrics`를 보내는 상황을 20초간 재현했습니다. 서버와 부하 생성기는 같은 PC(Windows)에서 실행했고, MySQL은 같은 PC의 Docker 컨테이너를 사용했습니다. 단계마다 테이블을 비우고 새 서버로 측정했습니다.

```powershell
dotnet run -c Release --project src/LoadTest -- --agents 1000 --duration 20 --url http://127.0.0.1:5000
```

### MySQL 측정 결과

| 가상 에이전트 | 처리량(건/초) | 평균(ms) | p95(ms) | p99(ms) | 실패 |
|---|---|---|---|---|---|
| 10대 | 10 | 0.4 | 0.8 | 1.3 | 0 |
| 100대 | 100 | 0.3 | 0.5 | 0.8 | 0 |
| 500대 | 500 | 0.3 | 0.5 | 0.9 | 0 |
| 1000대 | 1,000 | 0.3 | 0.6 | 1.5 | 0 |
| 2000대 | 1,999 | 0.5 | 1.0 | 2.2 | 0 |
| 5000대 | 4,993 | 0.8 | 1.9 | 4.7 | 0 |
| 10000대 | 9,982 | 1.7 | 4.1 | 7.4 | 0 |

2000, 5000, 10000대 단계에서는 부하가 끝난 뒤 MySQL의 저장 행 수를 세어 보낸 건수와 대조했고, 모두 성공 건수 + 예열 요청 1건과 일치했습니다(유실 0).

### SQLite 시기 개선 전후 (참고)

초기 구현은 SQLite였고, 같은 측정 방식으로 병목을 찾아 개선했습니다.

| 가상 에이전트 | 개선 전 | 개선 후 |
|---|---|---|
| 100대 | 86건/초 (목표 100), 평균 583ms | 미측정 |
| 500대 | 1건/초, 실패 9,888/10,000 | 미측정 |
| 1000대 | 0건/초, 전부 실패 | 1,000건/초, 평균 0.4ms, 실패 0 |
| 2000대 | 미측정 | 1,999건/초, 평균 0.5ms, p95 1.1ms, 실패 0 |
| 5000대 | 미측정 | 4,995건/초, 평균 0.8ms, p95 2.0ms, 실패 0 |

개선 후의 5000대 수치가 SQLite 4,995건/초, MySQL 4,993건/초로 같은 수준입니다. 이 측정 구간에서는 병목이 DB 종류가 아니라 요청 처리 방식(큐와 배치)에 있었다는 뜻이고, 그래서 DB 교체가 성능 하락 없이 가능했습니다. MySQL로 옮긴 이유는 성능이 아니라, DB를 별도 서버로 분리할 수 있는 구조와 인덱스/동시 접속을 다루는 운영 구조를 갖추기 위해서입니다.

### 병목을 찾은 과정

| 단계 | 가설 | 결과 |
|---|---|---|
| WAL 모드 + `synchronous=NORMAL` (시도) | 쓰기마다 디스크 동기화가 병목 | 판단 불가로 결과에서 제외. 당시 측정이 Docker Desktop 경유라 편차가 컸고, 변경이 코드에 반영되었는지도 확인하지 못했습니다 |
| 요청마다 개별 INSERT를 큐 + 단일 쓰기 작업 + 배치 트랜잭션으로 변경 | 동기 DB 쓰기가 스레드를 막아 스레드 풀이 고갈됨 | 1000대에서 0건/초(전부 실패)가 1,000건/초(실패 0)로 개선 |
| ASP.NET Core 요청 로그를 Warning으로 변경 | 요청당 6줄의 콘솔 로그가 초당 약 1,000건에서 천장이 됨 | 2000대 884건/초가 1,999건/초(실패 0)로 개선 |

### 측정의 한계

- 응답 시간은 "큐에 넣고 응답하기까지"입니다. 저장 완료 시점은 별도로 확인했습니다. 단계마다 부하 종료 후 MySQL의 저장 행 수를 세어 보낸 건수와 대조했습니다(부하 생성기가 연결 예열용 요청을 1건 따로 보내므로 행 수는 성공 건수보다 1 많습니다).
- 부하 생성기와 서버, DB가 같은 PC에서 돌기 때문에 서버 단독 최대치가 아닙니다. 10000대에서도 실패가 0이므로 아직 한계에 도달한 것이 아닙니다.
- Windows의 Docker Desktop을 거친 서버 측정은 같은 조건에서도 편차가 매우 커서(10대 평균 12ms가 393ms로 변동) 기준 수치에서 제외했습니다. 서버는 네이티브로 실행하고 MySQL만 컨테이너로 두었습니다.

## 설계 메모와 한계

- 에이전트 큐는 메모리에만 있어서, 서버가 꺼진 상태에서 에이전트까지 종료하면 대기 중이던 지표는 사라집니다.
- GET API와 허브는 인증이 없습니다. 내부망 사용을 가정한 구성입니다.
- 알람 임계치(80%)와 쿨다운(60초)은 서버 코드의 상수입니다.
- 서버는 지표를 큐에 넣고 먼저 응답한 뒤 백그라운드에서 저장합니다. 서버가 비정상 종료되면 저장 전의 지표(최대 큐 용량 10만 건)가 유실될 수 있습니다. 큐가 가득 차면 503을 돌려주고, 에이전트는 이를 실패로 보고 자체 버퍼에 보관했다가 재전송합니다.
- Windows에서 Docker Desktop을 쓸 때 `localhost`가 IPv6로 먼저 연결을 시도하다 지연되는 문제가 있어, 에이전트와 관제 화면의 기본 주소를 `127.0.0.1`로 두었습니다.

## 앞으로의 계획

- [ ] 에이전트 버퍼를 디스크에 저장 (에이전트 재시작에도 보존)
- [ ] 장비별 등록 토큰 인증
- [ ] 서버 큐의 영속화 (재시작 시 유실 방지)
- [x] Docker Compose 배포, 부하 테스트 수치 측정
- [x] 통합 테스트와 GitHub Actions CI
- [x] 입력 검증, 데이터 보존 기간 삭제
- [x] SQLite에서 MySQL 8.4로 이전 (인덱스, 배치 INSERT, 비동기 쿼리)
- [ ] AI 기반 이상 탐지
- [ ] 웹 대시보드

