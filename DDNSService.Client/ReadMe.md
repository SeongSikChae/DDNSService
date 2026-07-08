# DDNSService.Client

`DDNSService.Client`는 `DDNSService.Server`에 주기적으로 접속하여 **자신의 DNS 레코드를 최신 IP로 갱신하도록 요청**하는 동적 DNS(DDNS) 클라이언트입니다. Windows 서비스 또는 Linux systemd 서비스로 상시 구동되도록 설계되어 있습니다.

## 개요

- **역할**: 서버에 gRPC `Update`를 주기적으로 호출하여 자신의 DNS 레코드 갱신 요청
- **통신**: gRPC over HTTPS (클라이언트 인증서 기반 mTLS)
- **식별 방식**: 클라이언트 인증서에서 추출한 정보로 갱신 대상 지정
- **호스팅**: .NET Generic Host + Windows Service / systemd

## 대상 프레임워크

- **.NET 10.0** (`net10.0`)
- SDK: `Microsoft.NET.Sdk`, 출력 형식: `Exe`
- `ImplicitUsings` 및 `Nullable` 활성화

## 동작 방식

### 1. DNS 동기화 (`DynamicDnsSyncTask`)

- 클라이언트 인증서에서 정보를 추출해 `UpdateRequestProto`를 구성합니다.
  - `Id` ← 인증서의 **Email Name**(`X509NameType.EmailName`)
  - `Name` ← 인증서의 **Simple Name**(`X509NameType.SimpleName`)
- gRPC `DynamicDnsService.Update`를 호출하고, 응답(`UpdateResponseProto`)의 결과에 따라 로그를 기록합니다.
  - `error == true` → 오류 로그
  - `error == false` → 정보 로그

> 클라이언트는 IP를 직접 계산해 전송하지 않습니다. 서버가 gRPC 연결의 실제 원격 IP를 감지하므로, 클라이언트는 인증서 기반 식별 정보만 전달합니다.

### 2. 스케줄링 (`ClientHostedService`)

- 서비스가 시작되면 동기화 태스크를 **즉시 1회 실행**합니다.
- 실행 결과와 관계없이, 이후 Quartz Cron 표현식 `0 0/15 * * * ?`에 따라 **15분마다** 반복 실행하도록 스케줄러(`ITaskScheduler`)에 등록합니다.
- 서비스 종료 시 태스크를 제거하고 스케줄러가 정리될 때까지 대기합니다.

## 실행 방법

명령줄 인수로 설정 파일과 로그 디렉터리 경로를 지정해야 합니다 (둘 다 필수).

```bash
DDNSService.Client --config <설정파일경로> --log <로그디렉터리경로>
```

| 옵션 | 필수 | 설명 |
| --- | --- | --- |
| `--config` | 예 | YAML 설정 파일 경로 |
| `--log` | 예 | 로그 파일이 생성될 디렉터리 경로 |

## 설정 (YAML)

`--config`로 지정하는 YAML 파일은 `Configuration` 클래스로 역직렬화되며, 시작 시 `ConfigurationValidator`가 유효성을 검증합니다.

| 키 | 타입 | 필수 | 설명 |
| --- | --- | --- | --- |
| `ContentRootPath` | string | 예 | 애플리케이션 콘텐츠 루트 경로 (인증서 경로 기준) |
| `Address` | string | 예 | 접속할 서버의 gRPC 주소 (예: `https://host:port`) |
| `ClientCertificate` | string | 예 | 클라이언트 인증서(PKCS#12) 파일 경로 (`ContentRootPath` 기준 상대) |
| `ClientCertificatePassword` | string | 예 | 클라이언트 인증서 비밀번호 |

## 보안 / 통신

- 시작 시 `ClientCertificate`(PKCS#12)를 로드합니다.
- `HttpClientHandler`에 클라이언트 인증서를 추가하여 서버와 **상호 TLS(mTLS)** 연결을 수립합니다.
- 해당 핸들러로 `GrpcChannel`을 생성하고 `DynamicDnsServiceClient`를 구성합니다.
- 인증서는 서버 접속 인증 수단이자, 갱신 대상 식별 정보(`Id`, `Name`)의 출처로 함께 사용됩니다.

## 로깅

Serilog로 콘솔과 파일에 동시에 기록합니다.

- 최소 레벨: `Information`
- 호출자 정보 보강(`Enrich.WithCaller`)
- 파일: `--log` 디렉터리에 `DDNSService.Client.log`로 기록, **일 단위 롤링**, 최근 **12개** 파일 보관

## 의존성 주입 구성

`CreateHostApplication`에서 다음 서비스가 등록됩니다.

- `AddSystemd()`, `AddWindowsService()` — OS 서비스 통합
- `Configuration` (싱글턴)
- `X509Certificate2` — 로드한 클라이언트 인증서 (싱글턴)
- `GrpcChannel` 및 `DynamicDnsServiceClient` (싱글턴)
- `ITaskScheduler` → `DefaultTaskScheduler` (싱글턴)
- `DynamicDnsSyncTask` (싱글턴)
- `ClientHostedService` (HostedService)

## 주요 의존성

| 구성 요소 | 용도 |
| --- | --- |
| `DDNSService.Lib` (프로젝트 참조) | gRPC 계약(proto) 및 클라이언트 스텁, 로깅·설정·호스팅·명령줄 파싱 공통 기반 |

> `Grpc.Net.Client`, `CommandLineParser`, `YamlDotNet`, `Serilog`, 호스팅 관련 패키지는 `DDNSService.Lib`를 통해 전이적으로 제공됩니다.

## 프로젝트 구조

```
DDNSService.Client/
├── Program.cs                       # 진입점: 인수 파싱, 인증서/gRPC 채널/DI 구성
├── Configuration.cs                 # YAML 설정 모델 및 유효성 속성
├── Services/
│   └── ClientHostedService.cs       # 즉시 1회 실행 후 15분 주기 스케줄 등록/해제
├── Tasks/
│   └── DynamicDnsSyncTask.cs        # 인증서 정보로 gRPC Update 호출
└── ReadMe.md
```

## 빌드

```bash
dotnet build DDNSService.Client/DDNSService.Client.csproj
```
