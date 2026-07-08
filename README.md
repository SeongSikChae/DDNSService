# DDNSService

Azure DNS를 활용한 **동적 DNS(DDNS) 서비스**입니다. 클라이언트가 서버에 주기적으로 접속하면, 서버가 **연결의 실제 원격 IP**를 감지하여 Azure DNS Zone의 A/AAAA 레코드를 자동으로 갱신합니다.

## 특징

- 클라이언트가 IP를 직접 계산/전송하지 않고, **서버가 gRPC 연결의 원격 IP를 감지**하여 반영
- **상호 TLS(mTLS)** 기반의 안전한 gRPC 통신 (클라이언트 인증서 필수)
- 클라이언트 인증서 정보로 갱신 대상 DNS 레코드를 식별
- 30분 이상 갱신되지 않은 레코드를 자동 정리
- Windows 서비스 / Linux systemd 서비스로 상시 구동

## 아키텍처

```
┌─────────────────────┐        gRPC over mTLS         ┌─────────────────────┐        Azure SDK        ┌──────────────┐
│  DDNSService.Client │ ───────────────────────────▶  │  DDNSService.Server │ ──────────────────────▶ │  Azure DNS   │
│  (인증서로 식별 요청)│        Update(RPC)            │  (원격 IP → 레코드)  │   A/AAAA 레코드 갱신    │   Zone       │
└─────────────────────┘                               └─────────────────────┘                         └──────────────┘
             \                                                   /
              \             DDNSService.Lib (공유 계약/공통 기반)  /
               └─────────────────────────────────────────────────┘
```

## 프로젝트 구성

| 프로젝트 | 유형 | 설명 |
| --- | --- | --- |
| [`DDNSService.Lib`](DDNSService.Lib/README.md) | 공유 라이브러리 | gRPC 서비스 계약(proto)과 로깅·설정·호스팅 공통 의존성을 제공 |
| [`DDNSService.Server`](DDNSService.Server/ReadMe.md) | 서버 (실행) | gRPC 요청의 원격 IP를 Azure DNS 레코드에 반영, 만료 레코드 정리 |
| [`DDNSService.Client`](DDNSService.Client/ReadMe.md) | 클라이언트 (실행) | 인증서 정보로 서버에 주기적으로 갱신 요청 |

### DDNSService.Lib

- 클라이언트/서버가 공유하는 **gRPC 계약** 정의 (`DynamicDnsService.Update`, `UpdateRequestProto`, `UpdateResponseProto`)
- `Grpc.Tools`가 빌드 시 `.proto`로부터 서버/클라이언트 코드를 모두 생성(`GrpcServices="Both"`)
- 로깅(Serilog), 설정(YAML/Configuration), 호스팅(Windows Service/systemd), 명령줄 파싱 등 공통 의존성을 집약해 전이 제공

### DDNSService.Server

- 클라이언트의 `Update` 요청을 받아 **연결의 원격 IP**를 감지: IPv4 → A 레코드, IPv6 → AAAA 레코드로 생성/갱신 (TTL 3600초, `LastUpdateTime` 메타데이터 기록)
- `RecordExpirationTask`가 30분마다 실행되어, 마지막 갱신 후 30분 이상 경과한 레코드를 삭제
- Kestrel HTTPS: HTTP/2·HTTP/3, TLS 1.2/1.3, **클라이언트 인증서 필수(mTLS)**, 인증서 체인·Cipher Suite 설정 지원
- Azure 인증은 `ClientSecretCredential`(Tenant/Client/Secret) 기반 `ArmClient` 사용

### DDNSService.Client

- 클라이언트 인증서에서 `Id`(Email Name)와 `Name`(Simple Name)을 추출해 `Update` 호출
- 시작 시 즉시 1회 실행 후, **30분 주기**로 반복 동기화
- PKCS#12 인증서를 `HttpClientHandler`에 추가하여 서버와 mTLS 연결 수립

## 실행 방법

서버와 클라이언트 모두 설정 파일(`--config`)과 로그 디렉터리(`--log`) 경로를 필수 인수로 받습니다.

```bash
# 서버
DDNSService.Server --config <설정파일경로> --log <로그디렉터리경로>

# 클라이언트
DDNSService.Client --config <설정파일경로> --log <로그디렉터리경로>
```

설정은 YAML 파일로 작성하며, 시작 시 유효성 검증이 수행됩니다. 각 프로젝트의 설정 항목은 개별 README를 참고하세요.

## 공통 사양

- **대상 프레임워크**: .NET 10.0
- **통신**: gRPC over HTTPS (HTTP/2, HTTP/3), 상호 TLS
- **DNS 백엔드**: Azure DNS (`Azure.ResourceManager.Dns`)
- **스케줄링**: Quartz Cron (`0 0/30 * * * ?`, 30분 주기)
- **로깅**: Serilog (콘솔 + 일 단위 롤링 파일, 최근 12개 보관)
- **호스팅**: Windows 서비스 / Linux systemd

## 빌드

솔루션 전체를 빌드합니다.

```bash
dotnet build DDNSService.slnx
```

## 상세 문서

- [DDNSService.Lib README](DDNSService.Lib/README.md)
- [DDNSService.Server README](DDNSService.Server/ReadMe.md)
- [DDNSService.Client README](DDNSService.Client/ReadMe.md)
