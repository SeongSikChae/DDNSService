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

1. 요청의 `Name`이 설정된 `AzureDns.DnsZoneName`으로 끝나는지 검증 (영역 밖이면 오류 반환)
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
| `AzureDns` | object | 예 | Azure DNS 설정 (아래 표) |
| `ServerCertificate` | string | 예 | 서버 인증서(PKCS#12) 파일 경로 (`ContentRootPath` 기준 상대) |
| `ServerCertificatePassword` | string | 예 | 서버 인증서 비밀번호. 평문으로 넣으면 기동 시 `DEKENC:...`로 암호화되어 파일에 저장됨 |
| `CertificateChain` | string | 아니오 | 전송할 인증서 체인 이름 목록(쉼표 구분) |
| `IncludeCipherSuites` | string | 아니오 | 허용할 TLS Cipher Suite 목록(쉼표 구분) |
| `Kms` | object | 예 | 설정 보안정보 암호화 설정 (아래 표) |

`AzureDns` 섹션

| 키 | 타입 | 필수 | 설명 |
| --- | --- | --- | --- |
| `TenantId` | string | 예 | Azure AD 테넌트 ID |
| `ClientId` | string | 예 | 서비스 주체(App) 클라이언트 ID |
| `ClientSecret` | string | 예 | 서비스 주체 클라이언트 시크릿. 평문으로 넣으면 기동 시 `DEKENC:...`로 암호화되어 파일에 저장됨 |
| `ResourceId` | string | 예 | Azure 구독 리소스 ID |
| `ResourceGroupName` | string | 예 | DNS Zone이 속한 리소스 그룹 이름 |
| `DnsZoneName` | string | 예 | 대상 DNS Zone 이름 |

`Kms` 섹션

| 키 | 타입 | 필수 | 설명 |
| --- | --- | --- | --- |
| `DekFilePath` | string | 예 | DEK 파일 경로 (`ContentRootPath` 기준 상대). 없으면 최초 기동 시 생성 |
| `KekSalt` | string | 아니오 | KEK salt(Base64). 비우면 최초 기동 시 생성해 설정 파일에 저장 |
| `KekIterations` | int | 아니오 | KEK PBKDF2 반복 횟수 (기본 600000) |
| `CredentialStore` | string | 아니오 | OS 자격 증명 저장소 종류(`GCM_CREDENTIAL_STORE`). 비우면 OS 기본. Linux 헤드리스는 `gpg` 권장 |

> 이전 버전 설정의 최상위 `TenantId`~`DnsZoneName` 키는 더 이상 읽지 않습니다. `AzureDns` 섹션 아래로 옮겨야 합니다. 최상위에 남아 있으면 `Property 'TenantId' not found on type 'DDNSService.Server.Configuration'`(YAML 역직렬화 오류)로, `AzureDns` 섹션이 없으면 `config field 'AzureDns' must be provided`로 기동이 중단됩니다.

## 설정 보안정보 암호화

`ServerCertificatePassword`와 `AzureDns.ClientSecret`은 기동 시 [`Biz.Bizadm.KMS`](https://www.nuget.org/packages/Biz.Bizadm.KMS)로 보호됩니다.

- **KEK**: OS 자격 증명 금고(`Biz.Bizadm.KMS.Credentials`, service `ddnsservice-server://kek`, account `default`)의 패스워드 + `Kms.KekSalt`/`Kms.KekIterations`로 유도 (`AesGcmKekManager`)
- **DEK**: `Kms.DekFilePath` 파일에 KEK로 wrap되어 저장 (`AesGcmDekCipher`)
- **동작**
  1. 금고에 KEK 패스워드가 없고 DEK 파일도 없으면 랜덤 32바이트를 생성해 금고에 저장
  2. `KekSalt`가 비어 있으면 생성해 DEK 파일을 만들기 전에 설정 파일에 먼저 저장
  3. 대상 값이 `DEKENC:`로 시작하지 않으면(평문) 암호화해 설정 파일을 다시 씀
  4. 설정 파일은 임시 파일에 쓴 뒤 교체하며, 원본의 권한(Windows ACL / Unix 파일 모드)을 그대로 유지. 실패하면 임시 파일을 지우고 기동 중단
  5. 복호화한 값을 런타임에만 사용 (로그에 남기지 않음)

운영 시 주의사항:

- **실행 계정**: OS 금고는 계정별입니다. 최초 기동(암호화)과 서비스 실행을 같은 계정으로 해야 합니다.
- **Linux systemd**: 기본 저장소(Secret Service)는 헤드리스 서비스에서 동작하지 않을 수 있습니다. `Kms.CredentialStore: gpg`를 지정하고 서비스 계정으로 gpg 키 생성 및 `pass init <gpg-id>`를 먼저 수행하세요. 필요하면 `PASSWORD_STORE_DIR` 등은 systemd `Environment=`로 지정합니다. `cache`는 재시작 후 유지되지 않으므로 사용하지 말고, `plaintext`는 KEK 패스워드가 평문 파일로 남으므로 권장하지 않습니다. 구체적인 절차는 아래 "Linux systemd 배포"를 참고하세요.
- **변경 금지**: `KekSalt`·`KekIterations`를 바꾸면 기존 DEK를 열 수 없습니다.
- **재기록**: 설정 파일을 다시 쓸 때 주석과 서식은 사라집니다. 서비스 계정에 설정 파일 디렉터리 쓰기 권한이 필요합니다. 쓰기에 실패하면 기동이 중단되며, 권한을 고친 뒤 다시 기동하면 이어서 진행됩니다.
- **복구**: 금고 엔트리가 없는데 DEK 파일이 있으면 KEK를 새로 만들지 않고 기동을 중단합니다. 올바른 계정·저장소로 기동하거나, DEK 파일을 삭제하고 두 값을 평문으로 다시 넣은 뒤 기동하면 재암호화됩니다. 복호화 실패(변조·키 불일치) 시에도 기동이 중단됩니다.

## Linux systemd 배포

Rocky Linux 9에 `linux-x64` self-contained single-file로 배포해 `bizadm` 계정, 443 포트로 구동한 기준입니다. 계정·경로는 환경에 맞게 바꿉니다.

### 1. 필수 패키지

```bash
sudo dnf install -y libicu                  # 없으면 "Couldn't find a valid ICU package"로 즉시 종료
sudo dnf install -y epel-release            # pass는 EPEL에만 있음
sudo dnf install -y pass
```

HTTP/3(QUIC)까지 쓰려면 `libmsquic`이 필요합니다. 없으면 HTTP/2(TCP)만 리스닝합니다.

### 2. KEK 저장소 (`Kms.CredentialStore: gpg`)

**서비스 실행 계정으로** 수행합니다. 헤드리스 무인 기동을 위해 패스프레이즈 없는 키를 사용하며, 키 보호는 계정 권한(`~/.gnupg` 700)에 의존합니다.

```bash
gpg --batch --pinentry-mode loopback --passphrase '' \
    --quick-generate-key 'DDNSService.Server KEK store <bizadm@localhost>' rsa3072 encrypt never
pass init "$(gpg --list-secret-keys --with-colons bizadm@localhost | awk -F: '/^fpr:/ {print $10; exit}')"
```

첫 기동이 성공하면 `~/.password-store/Biz.Bizadm.KMS/ddnsservice-server/kek/default.gpg`가 생깁니다.

### 3. 파일 배치와 SELinux

```bash
sudo mkdir -p /DDNSService.Server && sudo chown bizadm:bizadm /DDNSService.Server
# publish 결과물과 config.yml 복사 후
mkdir -p /DDNSService.Server/log
chmod 755 /DDNSService.Server/DDNSService.Server
chmod 600 /DDNSService.Server/config.yml /DDNSService.Server/*.p12
# SELinux Enforcing이면 실행 파일에 bin_t 라벨 필요 (default_t면 systemd가 실행 불가)
sudo semanage fcontext -a -t bin_t '/DDNSService\.Server/DDNSService\.Server'
sudo restorecon -v /DDNSService.Server/DDNSService.Server
```

### 4. systemd 유닛 (`/etc/systemd/system/ddnsservice-server.service`)

```ini
[Unit]
Description=DDNSService.Server
Wants=network-online.target
After=network-online.target

[Service]
Type=notify
User=bizadm
Group=bizadm
WorkingDirectory=/DDNSService.Server
ExecStart=/DDNSService.Server/DDNSService.Server --config /DDNSService.Server/config.yml --log /DDNSService.Server/log
Environment=HOME=/home/bizadm
Environment=GPG_TTY=/dev/null
AmbientCapabilities=CAP_NET_BIND_SERVICE
UMask=0077
Restart=on-failure
RestartSec=10

[Install]
WantedBy=multi-user.target
```

| 항목 | 이유 |
| --- | --- |
| `Type=notify` | `AddSystemd()`가 기동 완료를 systemd에 통지 |
| `User=bizadm` | KEK가 저장된 gpg/pass 계정과 같아야 함 |
| `HOME` | single-file 실행과 gpg/pass가 홈 디렉터리를 찾도록 |
| `GPG_TTY` | GCM gpg 저장소가 이 값이 비어 있으면 `GPG_TTY is not set` 예외로 기동 중단. 패스프레이즈 없는 키라 실제 TTY는 쓰지 않음 |
| `AmbientCapabilities=CAP_NET_BIND_SERVICE` | 일반 계정으로 1024 미만 포트(443) 바인딩 |
| `UMask=0077` | 서비스가 새로 만드는 DEK·로그·재기록 설정 파일을 소유자 전용(600)으로 생성 |

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now ddnsservice-server
systemctl status ddnsservice-server
sudo journalctl -u ddnsservice-server -n 50
```

### 5. PQC 키 교환 (선택)

.NET은 TLS 키 교환 그룹을 코드로 지정하는 API가 없어 OpenSSL 시스템 정책을 따릅니다. Rocky 9의 `DEFAULT` 정책은 OpenSSL 3.5가 지원하는 ML-KEM 하이브리드 그룹을 포함하지 않으므로, 하이브리드 PQC(`X25519MLKEM768` 등)를 쓰려면 `PQ` 서브정책을 켭니다. 서버 전체(sshd 등)의 TLS/SSH 정책이 함께 바뀝니다.

```bash
sudo update-crypto-policies --set DEFAULT:PQ
sudo systemctl restart ddnsservice-server        # 정책은 프로세스 시작 시 적용 (재부팅 권장)
openssl s_client -connect 127.0.0.1:443 -groups X25519MLKEM768 </dev/null 2>&1 | grep 'Negotiated TLS1.3 group'
# Negotiated TLS1.3 group: X25519MLKEM768
```

PQ를 지원하지 않는 클라이언트는 기존 `X25519` 등으로 계속 접속됩니다.

### 6. 첫 기동 확인

설정 파일 내용을 출력하지 않고 다음으로 확인합니다.

- 로그에 `Now listening on: "https://[::]:443"`
- `config.yml` 수정 시각 변경(평문 → `DEKENC:` 재기록), 권한 유지
- `Kms.DekFilePath` 파일 생성
- `~/.password-store` 아래 KEK 항목 생성

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
| `Biz.Bizadm.KMS` | 설정 보안정보 암·복호화 (KEK/DEK) |
| `Biz.Bizadm.KMS.Credentials` | OS 자격 증명 금고 KEK 패스워드 저장 |
| `DDNSService.Lib` (프로젝트 참조) | gRPC 계약(proto), 로깅·설정·호스팅 공통 기반 |

## 프로젝트 구조

```
DDNSService.Server/
├── Program.cs                       # 진입점: 인수 파싱, 보안정보 암·복호화, Kestrel/TLS/DI 구성, gRPC 매핑
├── Configuration.cs                 # YAML 설정 모델(AzureDns·Kms 섹션 포함) 및 유효성 속성
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
