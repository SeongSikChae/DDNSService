# DDNSService.Server

`DDNSService.Server`는 클라이언트가 gRPC로 전달한 요청을 받아, **호출자의 실제 접속 IP를 Azure DNS Zone의 A/AAAA 레코드에 반영**하는 동적 DNS(DDNS) 서버입니다. Windows 서비스 또는 Linux systemd 서비스로 상시 구동되도록 설계되어 있습니다.

## 개요

- **역할**: 클라이언트의 공인 IP를 감지하여 Azure DNS 레코드를 자동 갱신
- **통신**: gRPC over HTTPS (HTTP/2, HTTP/3)
- **보안**: 상호 TLS(mTLS) — 클라이언트 인증서 필수
- **DNS 백엔드**: Azure DNS (`Azure.ResourceManager.Dns`)
- **호스팅**: ASP.NET Core Kestrel + Windows Service / systemd

## 대상 프레임워크

- **.NET 10.0** (`net10.0`)
- SDK: `Microsoft.NET.Sdk.Web`, 출력 형식: `Exe`
- `ImplicitUsings` 및 `Nullable` 활성화

## 동작 방식

### 1. IP 갱신 (gRPC `Update`)

클라이언트가 `DynamicDnsService.Update`를 호출하면 서버(`DynamicDnsServer`)는 다음을 수행합니다.

1. 요청의 `Name`이 설정된 `DnsZoneName`으로 끝나는지 검증 (영역 밖이면 오류 반환)
2. `Name`에서 존 이름을 제거해 레코드 이름을 계산
3. **gRPC 연결의 원격 IP 주소**를 확인 (IPv4-mapped IPv6는 IPv4로 변환)
   - IPv4 → **A 레코드** 생성/갱신
   - IPv6 → **AAAA 레코드** 생성/갱신
4. A/AAAA 레코드를 생성 또는 갱신합니다.
   - **신규 레코드**: DNS TTL **300초**를 설정합니다.
   - **기존 레코드**: 기존 TTL은 유지하고 IP 주소만 갱신합니다.
   - `LastUpdateTime` 메타데이터에 갱신 시각(Unix 밀리초, 로컬 기준)을 기록합니다.
   - **신규 레코드**에 한해 `Expiration` 메타데이터에 만료 기준 시간(초) **3600**을 설정합니다. 기존 레코드의 `Expiration` 값은 유지합니다.
5. 처리 결과를 `UpdateResponseProto`(`error`, `message`)로 응답

> DNS **TTL**(캐시 유효 시간)과 **Expiration**(서버 만료 판정 기준)은 별도로 관리됩니다. TTL은 DNS 조회 캐시용이며, 레코드 자동 삭제는 `Expiration` 메타데이터를 기준으로 합니다.

> 클라이언트가 별도로 IP를 보내지 않아도, 서버가 **연결의 실제 원격 IP**를 사용하는 점이 특징입니다.

### 2. 만료 레코드 정리 (`RecordExpirationTask`)

- Quartz Cron 표현식 `0 0/30 * * * ?`에 따라 **30분마다** 실행됩니다.
- DNS Zone의 모든 A/AAAA 레코드를 순회하며 `LastUpdateTime`, `Expiration` 메타데이터를 확인합니다.
- `LastUpdateTime` + `Expiration`(초 단위)이 현재 시각을 지난 레코드는 삭제하여, 더 이상 갱신되지 않는(오프라인) 항목을 정리합니다.
- `Expiration` 메타데이터가 없거나 파싱할 수 없는 레코드는 만료하지 않습니다.
- 스케줄링은 `ServerHostedService`(IHostedService)가 `ITaskScheduler`에 태스크를 등록/해제하며 관리합니다.

### 레코드 메타데이터 (`Consts`)

| 키 | 설정 시점 | 설명 |
| --- | --- | --- |
| `LastUpdateTime` | 매 갱신 | 마지막 `Update` 호출 시각(Unix 밀리초) |
| `Expiration` | 신규 레코드 생성 시 | 만료 판정 기준 시간(초). `LastUpdateTime` + `Expiration`이 지나면 `RecordExpirationTask`가 레코드를 삭제 |

## 실행 방법

명령줄 인수로 설정 파일과 로그 디렉터리 경로를 지정해야 합니다 (둘 다 필수).

```bash
DDNSService.Server --config <설정파일경로> --log <로그디렉터리경로>
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
| `Port` | ushort | 예 | Kestrel이 수신할 포트 |
| `TenantId` | string | 예 | Azure AD 테넌트 ID |
| `ClientId` | string | 예 | 서비스 주체(App) 클라이언트 ID |
| `ClientSecret` | string | 예 | 서비스 주체 클라이언트 시크릿 |
| `ResourceId` | string | 예 | Azure 구독 리소스 ID |
| `ResourceGroupName` | string | 예 | DNS Zone이 속한 리소스 그룹 이름 |
| `DnsZoneName` | string | 예 | 대상 DNS Zone 이름 |
| `ServerCertificate` | string | 예 | 서버 인증서(PKCS#12) 파일 경로 (`ContentRootPath` 기준 상대) |
| `ServerCertificatePassword` | string | 예 | 서버 인증서 비밀번호 |
| `CertificateChain` | string | 아니오 | 전송할 인증서 체인 이름 목록(쉼표 구분) |
| `IncludeCipherSuites` | string | 아니오 | 허용할 TLS Cipher Suite 목록(쉼표 구분) |

## 보안 / TLS 구성

Kestrel HTTPS 엔드포인트는 다음과 같이 구성됩니다.

- **프로토콜**: HTTP/2 + HTTP/3
- **클라이언트 인증서**: `RequireCertificate` — 클라이언트 인증서를 **반드시** 제출해야 함 (mTLS)
- **서버 인증서**: `ServerCertificate`(PKCS#12)를 로드하여 사용
- **인증서 체인**: `CertificateChain`이 설정된 경우, PKCS#12 컬렉션에서 개인 키가 없는 인증서를 이름으로 매칭해 체인을 구성
- **SSL 프로토콜**: TLS 1.2 / TLS 1.3
- **Cipher Suite**: `IncludeCipherSuites`가 지정되면 해당 목록으로 `CipherSuitesPolicy`를 적용하고 재협상(renegotiation)을 비활성화

## 로깅

Serilog로 콘솔과 파일에 동시에 기록합니다.

- 최소 레벨: `Information`
- 호출자 정보 보강(`Enrich.WithCaller`)
- 파일: `--log` 디렉터리에 `DDNSService.Server.log`로 기록, **일 단위 롤링**, 최근 **12개** 파일 보관

## 의존성 주입 구성

`CreateWebApplication`에서 다음 서비스가 등록됩니다.

- `AddSystemd()`, `AddWindowsService()` — OS 서비스 통합
- `AddGrpc()` — gRPC 서버
- `Configuration` (싱글턴)
- `ITaskScheduler` → `DefaultTaskScheduler` (싱글턴)
- `RecordExpirationTask` (싱글턴)
- `ArmClient` — `ClientSecretCredential`(TenantId/ClientId/ClientSecret) 기반 Azure 인증 (싱글턴)
- `ServerHostedService` (HostedService)

gRPC 엔드포인트는 `app.MapGrpcService<DynamicDnsServer>()`로 매핑됩니다.

## 주요 의존성

| 패키지 | 용도 |
| --- | --- |
| `Grpc.AspNetCore` | gRPC 서버 호스팅 |
| `Azure.Identity` | 서비스 주체(ClientSecret) 기반 Azure 인증 |
| `Azure.ResourceManager.Dns` | Azure DNS Zone/레코드 조회 및 갱신 |
| `DDNSService.Lib` (프로젝트 참조) | gRPC 계약(proto), 로깅·설정·호스팅 공통 기반 |

## 프로젝트 구조

```
DDNSService.Server/
├── Program.cs                       # 진입점: 인수 파싱, Kestrel/TLS/DI 구성, gRPC 매핑
├── Configuration.cs                 # YAML 설정 모델 및 유효성 속성
├── Consts.cs                        # 공통 상수 (메타데이터 키 등)
├── Services/
│   ├── DynamicDnsServer.cs          # gRPC Update 구현 (A/AAAA 레코드 반영)
│   └── ServerHostedService.cs       # 만료 태스크 스케줄 등록/해제
├── Tasks/
│   └── RecordExpirationTask.cs      # 30분마다 Expiration 메타데이터 기준 만료 레코드 삭제
└── ReadMe.md
```

## 빌드

```bash
dotnet build DDNSService.Server/DDNSService.Server.csproj
```
