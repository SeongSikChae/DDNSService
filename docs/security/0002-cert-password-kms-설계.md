---
status: done
category: security
feature: cert-password-kms
phase: 설계
---

# 설정 보안정보 KMS 암호화 — 설계

선행: [0001-cert-password-kms-방안.md](0001-cert-password-kms-방안.md)

## 구조

```text
Program.Main
 ├─ YAML 역직렬화 → ConfigurationValidator.Validate          (기존, 하위 섹션 검증 자동 포함)
 ├─ ProtectSecrets(configFileInfo, configuration)            (신규, Program 내부 private static)
 │    ├─ Kms.CredentialStore 지정 시 GCM_CREDENTIAL_STORE 설정
 │    ├─ OS 금고 KEK 패스워드 확보 (엔트리 없고 DEK 파일도 없을 때만 생성·저장, DEK 있으면 기동 중단)
 │    ├─ Kms.KekSalt 확보 (없으면 생성)
 │    ├─ AesGcmKekManager → AesGcmDekCipher(DEK 파일) → DekCipherService
 │    ├─ ServerCertificatePassword, AzureDns.ClientSecret: 평문이면 Encrypt
 │    ├─ (salt 생성 or 평문 암호화) 시에만 YAML 1회 재기록
 │    └─ 두 필드 Decrypt → 런타임 평문
 └─ CreateWebApplication / StartAsync                      (기존, AzureDns 필드 경로만 변경)
```

설정 모델 `KmsConfiguration`, `AzureDnsConfiguration` 외에 새 클래스는 만들지 않는다. 기동 1회만 쓰므로 KMS 객체는 메서드 안에서 `using`으로 해제한다(DI 등록 안 함).

## 설정 데이터 (Configuration.cs)

Azure DNS 설정과 KMS 설정을 중첩 섹션으로 둔다. 두 클래스는 `Configuration.cs`에 함께 정의한다.

```yaml
ContentRootPath: ...
Port: 8443
AzureDns:
  TenantId: 00000000-0000-0000-0000-000000000000
  ClientId: 11111111-1111-1111-1111-111111111111
  ClientSecret: your-azure-client-secret        # 기동 시 DEKENC:... 로 교체
  ResourceId: /subscriptions/22222222-2222-2222-2222-222222222222
  ResourceGroupName: my-dns-rg
  DnsZoneName: example.com
ServerCertificate: server.pfx
ServerCertificatePassword: your-server-cert-password   # 기동 시 DEKENC:... 로 교체
Kms:
  DekFilePath: ddns-server.dek
  KekSalt:            # 비워 두면 최초 기동 시 생성·저장
  KekIterations: 600000
  CredentialStore:    # 선택. 예: gpg (Linux 헤드리스). 비우면 OS 기본
```

`Configuration`

| 키 | 타입 | 필수 | 설명 |
| --- | --- | --- | --- |
| `AzureDns` | `AzureDnsConfiguration` | 예 | Azure DNS 설정 섹션 (기존 최상위 `TenantId`~`DnsZoneName` 이동) |
| `ServerCertificatePassword` | string | 예 | 평문 입력 시 기동 때 `DEKENC:...`로 교체 (기존 필드, 의미 확장) |
| `Kms` | `KmsConfiguration` | 예 | KMS 설정 섹션 |

`AzureDnsConfiguration` (`AzureDns` 섹션) — 모두 string, 필수, 기존 `[Property]` 그대로 이동

| 키 | 설명 |
| --- | --- |
| `TenantId` | Azure AD 테넌트 ID |
| `ClientId` | 서비스 주체 클라이언트 ID |
| `ClientSecret` | 서비스 주체 클라이언트 시크릿. 평문 입력 시 기동 때 `DEKENC:...`로 교체 |
| `ResourceId` | 구독 리소스 ID |
| `ResourceGroupName` | DNS Zone 리소스 그룹 |
| `DnsZoneName` | DNS Zone 이름 |

`KmsConfiguration` (`Kms` 섹션)

| 키 | 타입 | 필수 | 기본값 | 설명 |
| --- | --- | --- | --- | --- |
| `DekFilePath` | string | 예 | — | DEK envelope 파일 경로 (`ContentRootPath` 기준 상대, `ServerCertificate`와 동일 규칙). 없으면 최초 기동 시 생성 |
| `KekSalt` | string | 아니오 | (생성) | KEK PBKDF2 salt, Base64. 비어 있으면 16바이트 랜덤 생성 후 YAML에 저장 |
| `KekIterations` | int | 아니오 | 600000 | KEK PBKDF2 반복 횟수 (`DefaultValue`) |
| `CredentialStore` | string | 아니오 | (OS 기본) | Git Credential Manager 저장소 종류. 지정하면 기동 시 프로세스 환경 변수 `GCM_CREDENTIAL_STORE`를 이 값으로 설정(서비스 환경 변수보다 우선). 비우면 라이브러리 기본(Windows `wincredman`, macOS `keychain`, Linux `secretservice`). Linux 헤드리스 systemd는 `gpg`(gpg 키 + `pass init` 선행 필요) 권장. `cache`는 프로세스 재시작 후 유지되지 않으므로 사용 금지, `plaintext`는 KEK 패스워드가 평문 파일로 남으므로 비권장 |

`CredentialStore` 값은 검증하지 않고 GCM에 그대로 넘긴다. 잘못된 값이면 GCM 예외로 기동 중단된다. gpg·plaintext 저장소의 부가 환경 변수(`PASSWORD_STORE_DIR`, `GCM_PLAINTEXT_STORE_PATH` 등)는 서비스 환경(systemd `Environment=`)에서 지정한다.

검증: `PropertyType`에 객체 타입이 없으므로 `AzureDns`·`Kms` 속성에는 `[Property]`를 붙이지 않는다. `Configuration`이 `IValidatableConfiguration`을 구현해 `Validate()`에서 각 섹션이 null이면 예외, 아니면 `ConfigurationValidator.Validate(섹션)`으로 하위 필드를 검증한다. `ConfigurationValidator.Validate`는 끝에서 `IValidatableConfiguration.Validate()`를 자동 호출한다(라이브러리 소스 확인).

주의: `KekSalt`·`KekIterations`를 바꾸면 KEK `KeyId`가 달라져 기존 DEK 파일을 열 수 없다. 문서(ReadMe)에 명시한다.

## 참조 경로 변경

| 파일 | 변경 |
| --- | --- |
| `Program.cs` | `ClientSecretCredential(configuration.AzureDns.TenantId, ...ClientId, ...ClientSecret)` |
| `Services/DynamicDnsServer.cs` | `configuration.AzureDns.DnsZoneName/ResourceId/ResourceGroupName` |
| `Tasks/RecordExpirationTask.cs` | `configuration.AzureDns.ResourceId/ResourceGroupName/DnsZoneName` |

## OS 금고 키 (Consts.cs)

| 상수 | 값 |
| --- | --- |
| `KMS_KEK_SERVICE` | `ddnsservice-server://kek` |
| `KMS_KEK_ACCOUNT` | `default` |

Namespace는 라이브러리 기본값(`Biz.Bizadm.KMS`)을 쓴다. 앱 전용 service 이름으로 다른 앱의 KEK와 섞이지 않게 한다.

## 인터페이스·의존성

- 패키지: `Biz.Bizadm.KMS`, `Biz.Bizadm.KMS.Credentials` (`DDNSService.Server.csproj`).
- 사용 API
  - `OsKekCredentialProvider.CreateForCurrentOs(service, account)` → `IOsKekCredentialStore`
  - `IOsKekCredentialStore.GetPassword()` (없으면 `InvalidOperationException`) / `StorePassword(ReadOnlySpan<byte>)`
  - `AesGcmKekManager.Create(IKekCredentialProvider, byte[] salt, int iterations)`
  - `AesGcmDekCipher.Create(IKekCipher, FileInfo)`
  - `DekCipherService(IDekCipher, leaveOpen)` — `StartsWithFromPrefix` / `Encrypt` / `Decrypt`
- YAML 재기록: YamlDotNet `SerializerBuilder().ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)`.

## 결정 목록

| # | 결정 | 이유 |
| --- | --- | --- |
| D1 | KEK 패스워드 없으면 `RandomNumberGenerator.GetBytes(32)` → `StorePassword`, 사용 후 `ZeroMemory` | 사용자 결정, 수동 등록 불필요 |
| D1a | 생성 조건: `GetPassword()`의 `InvalidOperationException` 메시지가 `KEK credential not found`로 시작 **그리고** DEK 파일이 없을 때만. DEK 파일이 있으면 생성하지 않고 기동 중단. 그 외 예외는 그대로 전파 | 검토 R1: 일시 오류나 계정 불일치로 기존 KEK를 덮어써 복호화 불가가 되는 것을 방지 |
| D10 | `Kms.CredentialStore` 지정 시 `CreateForCurrentOs` 호출 전에 `Environment.SetEnvironmentVariable("GCM_CREDENTIAL_STORE", value)` | 검토 R2: 배포 프로필이 linux-x64 systemd라 Secret Service가 없을 수 있음. 라이브러리는 값이 비어 있을 때만 기본값을 넣으므로 이 값이 우선 |
| D2 | salt·iterations·DEK 경로는 YAML 중첩 `Kms` 섹션, 검증은 `IValidatableConfiguration`으로 위임 | 사용자 결정(중첩 객체), `PropertyType`에 객체 타입 없음 |
| D3 | YAML 재기록은 평문 암호화 또는 salt 생성 시에만, 여러 필드가 바뀌어도 1회 | 주석·서식 유실 최소화, 재기동 시 파일 불변 |
| D4 | 재기록은 같은 디렉터리 임시 파일에 쓴 뒤 `File.Move(overwrite: true)` | 쓰기 중 중단 시 설정 파일 손상 방지 |
| D5 | 복호화 실패(키 불일치·변조)는 예외 그대로 기동 중단, 평문 우회 없음 | 규칙: 평문 모드 기본 경로 금지 |
| D6 | 비밀번호·시크릿·KEK·salt 값은 로그에 남기지 않음 | 규칙 4 |
| D7 | PFX 로드·`ClientSecretCredential` 코드는 필드 경로만 바꾸고 로직은 유지 | 런타임에는 복호화된 평문이 들어 있음 |
| D8 | Azure DNS 6개 필드를 `AzureDns` 섹션으로 이동, 평면 키 하위 호환 없음 | 사용자 결정. 기존 설정 파일은 직접 이전 |
| D9 | encrypt-if-needed는 로컬 함수 `string EncryptIfNeeded(string value)` 하나로 두 필드에 적용 | 같은 로직 중복 방지, 새 타입 없음 |

## 기동 시나리오

| 상태 | 동작 | YAML 재기록 |
| --- | --- | --- |
| 최초: 평문 비밀번호·시크릿, `Kms.KekSalt` 없음, DEK·금고 엔트리 없음 | 금고 생성, salt 생성, DEK 생성, 두 필드 암호화 | 예 (1회) |
| 재기동: 두 필드 암호문, salt 있음 | 복호화만 | 아니오 |
| 운영자가 둘 중 하나를 평문으로 교체 | 해당 필드만 암호화 | 예 |
| DEK 파일 분실 | 새 DEK 생성 후 복호화 실패 → 기동 중단. 두 필드에 평문을 다시 넣으면 복구 | — |
| 금고 엔트리 분실·다른 계정으로 기동 (DEK 파일 존재) | KEK를 새로 만들지 않고 기동 중단(금고 엔트리 없음 메시지). 올바른 계정·`CredentialStore`로 기동하거나, DEK 파일 삭제 + 평문 재입력으로 복구 | — |
| 금고 조회 중 다른 오류 | 예외 전파로 기동 중단, KEK 변경 없음 | — |
| 기존 평면 키 YAML (`AzureDns` 없음) | `config field 'AzureDns' must be provided`로 기동 중단 | — |
