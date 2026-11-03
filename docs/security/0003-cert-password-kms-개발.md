---
status: done
# 2026-11-03 U6 추가: 구현 후 독립 검토(R-a salt 유실, R-d 권한 미보존)를 사용자 결정으로 반영
category: security
feature: cert-password-kms
phase: 개발
---

# 설정 보안정보 KMS 암호화 — 개발

선행: [0001-cert-password-kms-방안.md](0001-cert-password-kms-방안.md), [0002-cert-password-kms-설계.md](0002-cert-password-kms-설계.md)

## 확인 사항 (개발 단계 조사)

- nuget.org 최신: `Biz.Bizadm.KMS` **1.0.6**, `Biz.Bizadm.KMS.Credentials` **1.0.2** (둘 다 net10.0, 단일 major.minor 라인의 최고 patch).
- `ConfigurationValidator.Validate`는 마지막에 `config is IValidatableConfiguration`이면 `Validate()`를 **자동 호출**한다 → `Main`에서 별도 호출 불필요.
- `PropertyAttribute.DefaultValue`는 string이며 `PropertyType.INT`면 `int.Parse` 후 `int?` 속성에 설정된다.
- 네임스페이스: `Biz.Bizadm.KMS.Cipher`(`AesGcmKekManager`, `AesGcmDekCipher`, `DekCipherService`), `Biz.Bizadm.KMS.Credentials.Cipher`(`OsKekCredentialProvider`, `IOsKekCredentialStore`).
- Azure DNS 필드 참조처: `Program.cs:151`, `Services/DynamicDnsServer.cs:14,18,22,24-26`, `Tasks/RecordExpirationTask.cs:17-19`.

## 작업 단위

### U1. 패키지 참조

1. **대상**: `DDNSService.Server/DDNSService.Server.csproj`
2. **코드 초안**:
   ```powershell
   dotnet package add Biz.Bizadm.KMS --version 1.0.6 --project DDNSService.Server/DDNSService.Server.csproj
   dotnet package add Biz.Bizadm.KMS.Credentials --version 1.0.2 --project DDNSService.Server/DDNSService.Server.csproj
   ```
3. **검증**: `dotnet build DDNSService.Server` 성공, `dotnet package list --vulnerable --include-transitive` / `--deprecated` 에 신규 항목 없음
4. **선행 단위**: 없음

### U2. 설정 모델 + 참조 경로

1. **대상**: `DDNSService.Server/Configuration.cs` — `Configuration`(IValidatableConfiguration 구현, 6개 필드 제거, `AzureDns`·`Kms` 추가), 신규 `AzureDnsConfiguration`, `KmsConfiguration`. 참조처 `Program.cs`, `Services/DynamicDnsServer.cs`, `Tasks/RecordExpirationTask.cs`
2. **코드 초안**:
   ```csharp
   public sealed class Configuration : IValidatableConfiguration
   {
       // ... ContentRootPath, Port (existing) ...

       public AzureDnsConfiguration AzureDns { get; set; } = null!;

       // ... ServerCertificate, ServerCertificatePassword, CertificateChain, IncludeCipherSuites (existing) ...

       public KmsConfiguration Kms { get; set; } = null!;

       public void Validate()
       {
           if (AzureDns is null)
               throw new Exception("config field 'AzureDns' must be provided");
           ConfigurationValidator.Validate(AzureDns);

           if (Kms is null)
               throw new Exception("config field 'Kms' must be provided");
           ConfigurationValidator.Validate(Kms);
       }
   }

   public sealed class AzureDnsConfiguration
   {
       [Property(PropertyType.STRING, required: true)]
       public string TenantId { get; set; } = null!;
       // ... ClientId, ClientSecret, ResourceId, ResourceGroupName, DnsZoneName (기존 정의 그대로 이동) ...
   }

   public sealed class KmsConfiguration
   {
       [Property(PropertyType.STRING, required: true)]
       public string DekFilePath { get; set; } = null!;

       [Property(PropertyType.STRING, required: false)]
       public string? KekSalt { get; set; }

       [Property(PropertyType.INT, required: false, DefaultValue = "600000")]
       public int? KekIterations { get; set; }

       [Property(PropertyType.STRING, required: false)]
       public string? CredentialStore { get; set; }
   }
   ```
   ```csharp
   // Program.cs
   ClientSecretCredential credential = new ClientSecretCredential(configuration.AzureDns.TenantId, configuration.AzureDns.ClientId, configuration.AzureDns.ClientSecret);
   // DynamicDnsServer.cs / RecordExpirationTask.cs
   configuration.DnsZoneName → configuration.AzureDns.DnsZoneName   (ResourceId, ResourceGroupName 동일)
   ```
3. **검증**: 빌드 성공. `AzureDns` 누락 YAML → `config field 'AzureDns' must be provided`, `Kms.DekFilePath` 누락 → `config field 'DekFilePath' must be provided`로 기동 중단
4. **선행 단위**: 없음

### U3. OS 금고 키 상수

1. **대상**: `DDNSService.Server/Consts.cs`
2. **코드 초안**:
   ```csharp
   public const string KMS_KEK_SERVICE = "ddnsservice-server://kek";
   public const string KMS_KEK_ACCOUNT = "default";
   ```
3. **검증**: 빌드 성공
4. **선행 단위**: 없음

### U4. 기동 시 암·복호화

1. **대상**: `DDNSService.Server/Program.cs` — `Main`(호출 1줄), 신규 `private static void ProtectSecrets(FileInfo configFileInfo, Configuration configuration)`
2. **코드 초안**:
   ```csharp
   // Main
   ConfigurationValidator.Validate(configuration);
   ProtectSecrets(configFileInfo, configuration);
   WebApplication app = CreateWebApplication(args, cmdMain, configuration);
   ```
   ```csharp
   private static void ProtectSecrets(FileInfo configFileInfo, Configuration configuration)
   {
       bool rewrite = false;

       if (!string.IsNullOrWhiteSpace(configuration.Kms.CredentialStore))
           Environment.SetEnvironmentVariable("GCM_CREDENTIAL_STORE", configuration.Kms.CredentialStore);

       FileInfo dekFileInfo = new FileInfo(Path.Combine(configuration.ContentRootPath, configuration.Kms.DekFilePath));

       IOsKekCredentialStore credentialStore = OsKekCredentialProvider.CreateForCurrentOs(Consts.KMS_KEK_SERVICE, Consts.KMS_KEK_ACCOUNT);
       try
       {
           CryptographicOperations.ZeroMemory(credentialStore.GetPassword());
       }
       catch (InvalidOperationException e) when (e.Message.StartsWith("KEK credential not found", StringComparison.Ordinal))
       {
           if (dekFileInfo.Exists)
               throw new InvalidOperationException($"KEK credential not found but DEK file exists: {dekFileInfo.FullName}", e);

           byte[] password = RandomNumberGenerator.GetBytes(32);
           credentialStore.StorePassword(password);
           CryptographicOperations.ZeroMemory(password);
       }

       if (string.IsNullOrWhiteSpace(configuration.Kms.KekSalt))
       {
           configuration.Kms.KekSalt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
           rewrite = true;
       }

       using AesGcmKekManager kekManager = AesGcmKekManager.Create(credentialStore, Convert.FromBase64String(configuration.Kms.KekSalt), configuration.Kms.KekIterations!.Value);
       using AesGcmDekCipher dekCipher = AesGcmDekCipher.Create(kekManager.Current, dekFileInfo);
       using DekCipherService dekCipherService = new DekCipherService(dekCipher, leaveOpen: true);

       string EncryptIfNeeded(string value)
       {
           if (dekCipherService.StartsWithFromPrefix(value))
               return value;
           rewrite = true;
           return dekCipherService.Encrypt(value);
       }

       configuration.ServerCertificatePassword = EncryptIfNeeded(configuration.ServerCertificatePassword);
       configuration.AzureDns.ClientSecret = EncryptIfNeeded(configuration.AzureDns.ClientSecret);

       if (rewrite)
       {
           ISerializer serializer = new SerializerBuilder()
               .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
               .Build();
           string tempFilePath = configFileInfo.FullName + ".tmp";
           File.WriteAllText(tempFilePath, serializer.Serialize(configuration));
           File.Move(tempFilePath, configFileInfo.FullName, overwrite: true);
       }

       configuration.ServerCertificatePassword = dekCipherService.Decrypt(configuration.ServerCertificatePassword);
       configuration.AzureDns.ClientSecret = dekCipherService.Decrypt(configuration.AzureDns.ClientSecret);
   }
   ```
   - 순서 주의: `GCM_CREDENTIAL_STORE` 설정은 `CreateForCurrentOs` **전**(라이브러리가 비어 있을 때만 기본값을 넣음). YAML 재기록은 `Decrypt` **전**(파일에는 암호문만 저장).
   - KEK 생성은 "엔트리 없음" 메시지 + DEK 파일 없음일 때만(설계 D1a). 예외 메시지에는 경로만 넣고 비밀값은 넣지 않는다.
   - salt 생성 후 KEK/DEK 생성이 실패하면 파일은 아직 안 바뀐 상태로 기동 중단 → 다음 기동에서 다시 생성(일관성 유지).
   - 값은 로그로 출력하지 않는다.
3. **검증**: 빌드 성공 후 아래 "통합 검증" 1~3
4. **선행 단위**: U1, U2, U3

### U5. 문서

1. **대상**: `DDNSService.Server/ReadMe.md`(설정 표·KMS 절·DI 설명), `README.md`(설정 안내 12~13단계, 서버 설정 예시)
2. **코드 초안**:
   ```markdown
   | `AzureDns` | object | 예 | Azure DNS 설정 (아래 표) |
   | `ServerCertificatePassword` | string | 예 | 서버 인증서 비밀번호. 평문으로 넣으면 기동 시 `DEKENC:...`로 암호화되어 파일에 저장됨 |
   | `Kms` | object | 예 | KMS 설정 (아래 표) |

   | `AzureDns.ClientSecret` | string | 예 | 서비스 주체 클라이언트 시크릿. 기동 시 `DEKENC:...`로 암호화 |
   | `Kms.DekFilePath` | string | 예 | DEK 파일 경로 (`ContentRootPath` 기준 상대) |
   | `Kms.KekSalt` | string | 아니오 | KEK salt(Base64). 비우면 최초 기동 시 생성 |
   | `Kms.KekIterations` | int | 아니오 | KEK PBKDF2 반복 횟수 (기본 600000) |
   | `Kms.CredentialStore` | string | 아니오 | GCM 저장소 종류(`GCM_CREDENTIAL_STORE`). Linux 헤드리스는 `gpg` 권장 |
   ```
   KMS 절: OS 자격 증명 금고(`ddnsservice-server://kek`) 사용, 서비스 실행 계정과 최초 기동 계정 일치 필요, Linux systemd에서 `CredentialStore: gpg` + gpg 키·`pass init` 준비 절차(`cache` 금지, `plaintext` 비권장), salt/iterations 변경 금지, 재기록 시 주석 유실, 복구 절차, 기존 평면 키 설정 이전 안내.
3. **검증**: 표·예시가 `Configuration.cs`와 일치
4. **선행 단위**: U4

### U6. 재기록 안정성 (구현 후 독립 검토 반영)

1. **대상**: `DDNSService.Server/Program.cs` — `ProtectSecrets` 순서 변경, 신규 `private static void WriteConfiguration(FileInfo configFileInfo, Configuration configuration)`
2. **코드 초안**:
   ```csharp
   // ProtectSecrets: salt는 DEK 파일 생성 전에 먼저 저장
   if (string.IsNullOrWhiteSpace(configuration.Kms.KekSalt))
   {
       configuration.Kms.KekSalt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
       WriteConfiguration(configFileInfo, configuration);
   }
   // ... KEK/DEK 생성, EncryptIfNeeded ...
   if (rewrite)
       WriteConfiguration(configFileInfo, configuration);
   ```
   ```csharp
   private static void WriteConfiguration(FileInfo configFileInfo, Configuration configuration)
   {
       ISerializer serializer = new SerializerBuilder()
           .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
           .Build();
       string tempFilePath = configFileInfo.FullName + ".tmp";
       try
       {
           File.WriteAllText(tempFilePath, serializer.Serialize(configuration));
           if (OperatingSystem.IsWindows())
               new FileInfo(tempFilePath).SetAccessControl(configFileInfo.GetAccessControl());
           else
               File.SetUnixFileMode(tempFilePath, File.GetUnixFileMode(configFileInfo.FullName));
           File.Move(tempFilePath, configFileInfo.FullName, overwrite: true);
       }
       catch
       {
           File.Delete(tempFilePath);
           throw;
       }
   }
   ```
   - 검토 R-a: 재기록 실패 시 DEK 파일이 아직 없으므로 다음 기동에서 정상 재시도된다. 최초 기동은 salt 저장 + 암호문 저장으로 파일을 최대 2회 쓴다.
   - 검토 R-d: 원본의 Windows ACL / Unix 파일 모드를 임시 파일에 복사한 뒤 교체한다.
   - 실패 시 임시 파일(평문이 들어 있을 수 있음)을 지운다.
3. **검증**: 빌드, 통합 검증 1~3 재수행. 추가로 (a) 설정 파일 읽기 전용 + salt 없음으로 기동 → 기동 중단, DEK 파일·`.tmp` 없음, 읽기 전용 해제 후 재기동 성공. (b) 설정 파일에 보호된 명시 ACL 설정 후 최초 기동 → 재기록 전후 SDDL 동일. Unix 모드 보존은 Linux 미검증.
4. **선행 단위**: U4

## 통합 검증 (U4 이후)

운영 설정은 건드리지 않는다. 스크래치 디렉터리에 임시 YAML·임시 자체 서명 PFX·`ContentRootPath`를 만들고, Azure 값은 더미로 둔다(Kestrel 리스닝까지만 확인. `ClientSecretCredential`·`ArmClient`는 생성만 하므로 더미로 기동 가능한지 실행으로 확인).

1. **최초 기동**: 두 필드 평문·salt 없음 → Kestrel 리스닝 확인 후 종료. YAML의 `ServerCertificatePassword`, `AzureDns.ClientSecret`이 `DEKENC:` 암호문으로, `Kms.KekSalt` 생성, DEK 파일 생성, Windows 자격 증명 관리자에 `ddnsservice-server://kek` 엔트리 존재.
2. **재기동**: YAML 해시·수정 시각 불변, 정상 리스닝.
3. **음성**: 암호문 한 글자 변조 → 복호화 예외로 기동 중단(평문 우회 없음). `AzureDns` 섹션 누락 → 검증 예외. 테스트용 금고 엔트리만 삭제하고 DEK 파일은 둔 채 기동 → KEK 재생성 없이 기동 중단. 로그·콘솔에 평문 미노출.
5. **미검증 범위**: Linux(`secretservice`/`gpg`)·macOS 저장소와 linux-x64 single-file 배포는 이 Windows PC에서 실행 검증하지 않는다. 최종 보고에 미검증으로 명시.
4. **정리**: 검증으로 생성된 OS 금고 엔트리는 운영과 같은 이름이므로, 삭제 여부를 사용자에게 확인 후 처리.
