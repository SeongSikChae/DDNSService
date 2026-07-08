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
- 시작 시 즉시 1회 실행 후, **15분 주기**로 반복 동기화
- PKCS#12 인증서를 `HttpClientHandler`에 추가하여 서버와 mTLS 연결 수립

## Azure DNS 구성 방법

1. [Azure Portal](https://portal.azure.com/)에서 Azure 계정을 생성합니다.
2. **리소스 관리자 / 구독**에서 구독을 추가합니다.
3. **App Service 도메인** 을 통해 DNS를 구매합니다.
4. Azure 리소스에 접근할 인증 정보를 발급받기 위해 [Microsoft Entra 관리 센터](https://entra.microsoft.com/)에 접속합니다.
5. Entra 관리 센터 좌측 메뉴에서 **앱 등록** 메뉴로 이동합니다.
6. **새 등록** 버튼을 클릭하고 Application 명칭을 입력한 뒤, 지원되는 계정 유형을 **단일 테넌트만 - 기본 디렉토리**로 지정하고 등록합니다.
7. 해당 앱 설정의 **인증서 및 암호** 메뉴 > **클라이언트 비밀** 탭에서 새 클라이언트 암호를 등록합니다. (암호 등록 시 생성된 값은 이후 다시 확인할 수 없으므로 **반드시 따로 기록**해 둡니다.)
8. [Azure Portal](https://portal.azure.com/)로 돌아와서 리소스 그룹의 **액세스 제어(IAM)** 메뉴로 이동합니다.
9. **역할 할당** 탭에서 **추가** 버튼을 클릭하여 역할 할당을 추가합니다. 작업 기능 역할은 **Reader(독자)**로 지정하고, 구성원은 **사용자, 그룹 또는 서비스 주체**를 선택한 뒤 **구성원 선택**을 눌러 Application 이름을 검색하여 선택하고 할당합니다.
10. **DNS 영역**으로 돌아와서 **액세스 제어(IAM)** 메뉴로 이동합니다.
11. 역할에서 **DNS 영역 참가자**를 검색하여 선택하고, 구성원은 9번과 마찬가지로 Application 이름을 검색하여 선택하고 할당합니다.
12. Microsoft Entra 관리 센터에서 등록한 앱 **개요**의 **디렉터리(테넌트) ID**, **애플리케이션(클라이언트) ID**, **클라이언트 비밀 등록 시 생성된 값**을 `DDNSService.Server` 설정 파일에 등록합니다. (각각 `TenantId`, `ClientId`, `ClientSecret`에 해당)
13. Azure Portal 구독에서 **구독 ID**를 서버 설정 `ResourceId`에 `/subscriptions/구독 ID` 형식으로, DNS 영역이 위치한 **리소스 그룹 이름**을 `ResourceGroupName`으로, **DNS 명칭**을 `DnsZoneName`으로 지정합니다.

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
- **스케줄링**: Quartz Cron — 클라이언트 동기화 15분 주기(`0 0/15 * * * ?`), 서버 만료 정리 30분 주기(`0 0/30 * * * ?`)
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

## 동작 예시

아래는 서버와 클라이언트를 구동하는 예시입니다. 실제 값(도메인, 인증서 경로, Azure 자격 증명 등)은 환경에 맞게 교체하세요.

### DDNSService.Client

클라이언트 설정 파일 예시 (`client.yaml`):

```yaml
ContentRootPath: /opt/ddns/client
Address: https://ddns.example.com:5001
ClientCertificate: certs/client.pfx
ClientCertificatePassword: your-client-cert-password
```

실행:

```bash
DDNSService.Client --config /opt/ddns/client/client.yaml --log /var/log/ddns/client
```

동작 흐름:

1. `client.pfx` 인증서를 로드하고, 인증서의 Email Name → `Id`, Simple Name → `Name`으로 요청을 구성합니다.
2. 시작 즉시 서버에 `Update`를 1회 호출한 뒤, 15분마다 반복 호출합니다.
3. 결과 로그 예시:

```text
[INF] 'home' - 203.0.113.10 에 대해 A Record가 반영되었습니다.
```

### DDNSService.Server

서버 설정 파일 예시 (`server.yaml`):

```yaml
ContentRootPath: /opt/ddns/server
Port: 5001
TenantId: 00000000-0000-0000-0000-000000000000
ClientId: 11111111-1111-1111-1111-111111111111
ClientSecret: your-azure-client-secret
ResourceId: /subscriptions/22222222-2222-2222-2222-222222222222
ResourceGroupName: my-dns-rg
DnsZoneName: example.com
ServerCertificate: certs/server.pfx
ServerCertificatePassword: your-server-cert-password
CertificateChain: Intermediate CA, Root CA
IncludeCipherSuites: TLS_AES_256_GCM_SHA384, TLS_AES_128_GCM_SHA256
```

실행:

```bash
DDNSService.Server --config /opt/ddns/server/server.yaml --log /var/log/ddns/server
```

동작 흐름:

1. 지정한 포트에서 mTLS(클라이언트 인증서 필수) 기반 gRPC 요청을 수신합니다.
2. 클라이언트의 `Update` 요청을 받으면 **연결의 원격 IP**를 감지해 Azure DNS에 A/AAAA 레코드를 반영합니다.
3. 30분마다 만료 레코드(마지막 갱신 후 30분 이상 경과)를 정리합니다.
4. 결과 로그 예시:

```text
[INF] REQUEST Name: 'home', Address: '203.0.113.10', Type: A
[INF] Updated. Name: 'home', Address: '203.0.113.10', Type: A
[INF] A Record : 'office' Expired.
```

## DDNS 검증 방법

클라이언트 실행 후 DNS 레코드가 정상적으로 반영되었는지 아래 방법으로 확인할 수 있습니다. (예시 도메인: `home.example.com`)

### 1. DNS 조회로 확인

DNS 서버에 실제 반영된 레코드를 조회합니다. 클라이언트가 접속한 공인 IP와 일치하는지 확인합니다.

```bash
# nslookup (Windows/Linux 공통) - A 레코드(IPv4)
nslookup -type=A home.example.com

# nslookup - AAAA 레코드(IPv6)
nslookup -type=AAAA home.example.com

# dig (Linux/macOS)
dig +short A home.example.com
dig +short AAAA home.example.com
```

> DNS 캐시나 TTL(3600초)의 영향으로 즉시 반영되지 않을 수 있습니다. 권한 있는 네임서버로 직접 질의하면 캐시 영향을 줄일 수 있습니다.
>
> ```bash
> dig +short A home.example.com @ns1-XX.azure-dns.com
> ```

### 2. 로그로 확인

- **클라이언트 로그** (`DDNSService.Client.log`): 서버 응답 메시지로 반영 성공 여부를 확인합니다.

```text
[INF] 'home' - 203.0.113.10 에 대해 A Record가 반영되었습니다.
```

- **서버 로그** (`DDNSService.Server.log`): 요청 수신 및 레코드 반영 로그를 확인합니다.

```text
[INF] REQUEST Name: 'home', Address: '203.0.113.10', Type: A
[INF] Updated. Name: 'home', Address: '203.0.113.10', Type: A
```

### 3. Azure에서 직접 확인

Azure Portal 또는 Azure CLI로 DNS Zone의 레코드를 직접 조회합니다. 클라이언트의 IP 및 `LastUpdateTime` 메타데이터가 갱신되는지 확인합니다.

```bash
# A 레코드 조회
az network dns record-set a show \
  --resource-group my-dns-rg \
  --zone-name example.com \
  --name home

# 존의 모든 레코드 목록
az network dns record-set list \
  --resource-group my-dns-rg \
  --zone-name example.com \
  --output table
```

### 4. 만료 동작 확인

클라이언트를 중지하면 더 이상 갱신 요청이 발생하지 않습니다. 마지막 갱신 후 **30분 이상 경과**하면 서버의 정리 태스크가 해당 레코드를 삭제하며, 서버 로그에 다음과 같이 기록됩니다.

```text
[INF] A Record : 'home' Expired.
```

이후 DNS 조회 시 레코드가 조회되지 않으면 만료 정리가 정상 동작한 것입니다.
