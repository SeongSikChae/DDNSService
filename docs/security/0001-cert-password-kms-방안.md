---
status: done
category: security
feature: cert-password-kms
phase: 방안
---

# 설정 보안정보 KMS 암호화 — 방안

## 문제

`DDNSService.Server`의 설정 YAML에 `ServerCertificatePassword`(서버 PKCS#12 비밀번호)와 Azure 서비스 주체 `ClientSecret`이 평문으로 상시 저장된다. 설정 파일이 유출되면 인증서 개인키와 Azure DNS 변경 권한이 바로 노출된다.

## 목표

- 보안정보 대상: `ServerCertificatePassword`, `ClientSecret`.
- 기동 시 대상 필드가 평문이면 암호화해 설정 파일에 다시 저장한다.
- 이미 암호문이면 파일은 건드리지 않고 복호화해서 런타임(PFX 로드, `ClientSecretCredential`)에만 쓴다.
- Azure DNS 관련 설정(`TenantId`, `ClientId`, `ClientSecret`, `ResourceId`, `ResourceGroupName`, `DnsZoneName`)은 `AzureDns` 섹션으로 묶는다.
- 암·복호화는 `Biz.Bizadm.KMS` / `Biz.Bizadm.KMS.Credentials`를 쓴다. 자체 암호 유틸을 만들지 않는다.

## 대안

| 안 | KEK 위치 | 장점 | 단점 |
| --- | --- | --- | --- |
| A. Azure Key Vault KEK (`AzureKeyVaultKekManager`) | Key Vault RSA 키 | 키 물질이 Vault 밖으로 안 나옴, 저장소 규칙 기본안 | Key Vault·클라이언트 인증서·지문 설정 필요, 기동 시 네트워크 의존 |
| B. OS 자격 증명 금고 KEK (`AesGcmKekManager` + `OsKekCredentialProvider.CreateForCurrentOs()`) | Windows Credential Manager / macOS Keychain / Linux Secret Service | 외부 의존 없음, 설정 추가 최소 | KEK 패스워드가 실행 계정의 OS 금고에 묶임 |

## 채택 방안

**B. OS 자격 증명 금고 KEK** (사용자 지정).

흐름:

1. YAML 로드 → `ConfigurationValidator.Validate`
2. `OsKekCredentialProvider.CreateForCurrentOs(...)`로 OS 금고 제공자 생성
   - 엔트리가 없으면(`GetPassword`가 `InvalidOperationException`) 랜덤 32바이트를 만들어 `StorePassword`
3. `AesGcmKekManager.Create(provider, salt, iterations)` → `AesGcmDekCipher.Create(manager.Current, DEK 파일)` → `DekCipherService`
   - salt·iterations·DEK 파일 경로는 설정 YAML 필드로 둔다. salt가 비어 있으면 최초 기동 시 생성해 YAML에 저장
4. 대상 필드마다 `StartsWithFromPrefix`(`DEKENC:`)가 아니면 `Encrypt` → YAML 재기록(한 번)
5. `Decrypt` 결과를 런타임 값으로 사용 (기존 PFX 로드·`ClientSecretCredential` 코드는 필드 경로만 변경)

## 비범위

- `ServerCertificatePassword`·`ClientSecret` 외 다른 필드 암호화
- 기존 평면 키(`TenantId` 등) YAML 하위 호환 — 설정 파일을 `AzureDns` 섹션으로 직접 옮겨야 함
- KEK/DEK 로테이션
- `DDNSService.Client` 변경

## 리스크

- **실행 계정별 금고**: Windows 서비스(LocalSystem 등)·systemd 서비스 계정마다 OS 금고가 다르다. 최초 기동(암호화)과 서비스 실행을 같은 계정으로 해야 한다. 다른 계정에서 기동하면 KEK가 새로 생성되어 기존 DEK 복호화에 실패한다.
- **Linux 헤드리스**: Secret Service가 없으면 `GCM_CREDENTIAL_STORE=gpg` 등 사전 설정이 필요하다.
- **YAML 재기록**: YamlDotNet `Serializer`로 다시 쓰면 기존 주석·서식이 사라진다. 재기록은 평문 암호화 또는 salt 생성 시에만 한다.
- **복구 불가**: OS 금고 엔트리나 DEK 파일을 잃으면 암호문을 복호화할 수 없다. 이때는 YAML에 평문 비밀번호를 다시 넣고 기동하면 재암호화된다.
