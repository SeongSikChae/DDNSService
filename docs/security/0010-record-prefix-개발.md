---
status: done
category: security
feature: record-prefix
phase: 개발
---

# 등록 레코드 이름 설정화 — 개발

선행: [0008-record-prefix-방안.md](0008-record-prefix-방안.md), [0009-record-prefix-설계.md](0009-record-prefix-설계.md)

## 확인 사항 (개발 단계 조사)

- 서버 `Configuration`의 하위 섹션은 `Validate()`에서 null 검사 후 `ConfigurationValidator.Validate(섹션)`로 검증한다(`AzureDns`, `Kms`). 목록 항목도 같은 방식으로 항목마다 호출한다.
- `PropertyAttribute`는 스칼라 타입용이다. `RecordNames`(string 목록)의 필수·비어 있음 검사는 `Validate()`에서 직접 한다.
- 서버는 KMS 처리 중 YAML을 다시 쓸 수 있다(`Serializer`). `List<T>`는 그대로 직렬화된다.
- Client `Configuration`은 이미 DI 싱글턴으로 등록되어 있어 `DynamicDnsSyncTask` 생성자에 주입할 수 있다.
- 문서 참조처: `DDNSService.Client/ReadMe.md:23-24,68`, `DDNSService.Server/ReadMe.md:25-26`, 서버 설정 표, `README.md:49,128` 및 YAML 예시, `DDNSService.Lib/README.md:52,85,98-99`.

## 작업 단위

### U1. Client 설정·요청

1. **대상**: `DDNSService.Client/Configuration.cs`, `DDNSService.Client/Tasks/DynamicDnsSyncTask.cs`
2. **코드 초안**:
   ```csharp
   // Configuration.cs
   [Property(PropertyType.STRING, required: true)]
   public string RecordName { get; set; } = null!;
   ```
   ```csharp
   // DynamicDnsSyncTask.cs
   internal class DynamicDnsSyncTask(Configuration configuration, X509Certificate2 certificate, DynamicDnsService.DynamicDnsServiceClient client, ILogger<DynamicDnsSyncTask> logger) : AsyncTask
   // ...
   UpdateRequestProto req = new UpdateRequestProto
   {
       Id = certificate.GetNameInfo(X509NameType.EmailName, false),
       Name = configuration.RecordName
   };
   ```
3. **검증**: `dotnet build DDNSService.Client` 종료 코드 0. `RecordName` 누락 설정으로 기동 시 `ConfigurationValidator` 오류로 중단.
4. **선행 단위**: 없음

### U2. 서버 허용 목록 설정

1. **대상**: `DDNSService.Server/Configuration.cs`
2. **코드 초안**:
   ```csharp
   public List<AllowedClientConfiguration> AllowedClients { get; set; } = null!;

   public void Validate()
   {
       // ... existing AzureDns, Kms ...
       if (AllowedClients is null)
           throw new Exception("config field 'AllowedClients' must be provided");
       foreach (AllowedClientConfiguration allowedClient in AllowedClients)
       {
           ConfigurationValidator.Validate(allowedClient);
           if (allowedClient.RecordNames is null || allowedClient.RecordNames.Count == 0)
               throw new Exception($"config field 'AllowedClients[{allowedClient.Thumbprint}].RecordNames' must be provided");
       }
   }

   public sealed class AllowedClientConfiguration
   {
       [Property(PropertyType.STRING, required: true)]
       public string Thumbprint { get; set; } = null!;

       public List<string> RecordNames { get; set; } = null!;
   }
   ```
3. **검증**: U3과 함께 `dotnet build DDNSService.Server`. 파싱은 U4에서 확인.
4. **선행 단위**: 없음

### U3. 서버 권한 확인

1. **대상**: `DDNSService.Server/Services/DynamicDnsServer.cs` — `Update`
2. **코드 초안**:
   ```csharp
   private static readonly Regex RecordNameRegex = new Regex("^[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?$", RegexOptions.Compiled);

   public override async Task<UpdateResponseProto> Update(UpdateRequestProto request, ServerCallContext context)
   {
       HttpContext httpContext = context.GetHttpContext();
       X509Certificate2 clientCertificate = httpContext.Connection.ClientCertificate!;
       string name = request.Name;

       if (!RecordNameRegex.IsMatch(name))
       {
           logger.Warning($"REJECTED Invalid name. Thumbprint: '{clientCertificate.Thumbprint}', Name: '{name}'");
           return new UpdateResponseProto { Error = true, Message = $"'{name}' 은(는) 올바른 레코드 이름이 아닙니다." };
       }

       bool allowed = configuration.AllowedClients.Any(allowedClient =>
           string.Equals(allowedClient.Thumbprint, clientCertificate.Thumbprint, StringComparison.OrdinalIgnoreCase)
           && allowedClient.RecordNames.Contains(name, StringComparer.OrdinalIgnoreCase));
       if (!allowed)
       {
           logger.Warning($"REJECTED Not allowed. Thumbprint: '{clientCertificate.Thumbprint}', Name: '{name}'");
           return new UpdateResponseProto { Error = true, Message = $"'{name}' 에 대한 갱신 권한이 없습니다." };
       }

       SubscriptionResource subscription = ...  // ... existing (EndsWith 검사·Replace 삭제, httpContext 재사용) ...
   ```
   `logger.Warning`/`logger.Information` 확장은 기존 코드와 같은 것을 쓴다.
3. **검증**: `dotnet build DDNSService.Server` 종료 코드 0, 경고 증가 없음
4. **선행 단위**: U2

### U4. 서버 설정 파싱 확인

1. **대상**: 검증 전용 (코드 변경 없음)
2. **코드 초안**:
   ```csharp
   // scratchpad 스크립트: 서버 Configuration 타입으로 예시 YAML 역직렬화 후 Validate
   Configuration c = new Deserializer().Deserialize<Configuration>(yaml);
   ConfigurationValidator.Validate(c);
   ```
3. **검증**: (a) 정상 예시 통과 (b) `AllowedClients` 누락 → 예외 (c) `RecordNames` 빈 목록 → 예외 (d) `Thumbprint` 누락 → 예외
4. **선행 단위**: U2

### U5. 문서

1. **대상**: `DDNSService.Client/ReadMe.md`, `DDNSService.Server/ReadMe.md`, `README.md`, `DDNSService.Lib/README.md`
2. **코드 초안**:
   ```markdown
   | `RecordName` | string | 예 | 등록할 레코드 이름 prefix. 예: `home` → `home.<DnsZoneName>` |
   | `AllowedClients` | list | 예 | 클라이언트 인증서 지문별 갱신 허용 레코드 이름 목록 |

   1. 요청 `Name`이 DNS 레이블 규칙에 맞는지 검증
   2. TLS 클라이언트 인증서 지문이 `AllowedClients`에 있고 `RecordNames`에 `Name`이 포함되는지 확인 (없으면 오류 반환)
   ```
3. **검증**: `Simple Name`/`EndsWith`/"존 이름을 제거" 등 이전 설명이 docs/security 이력 문서 외에 남지 않음
4. **선행 단위**: U1, U3

## 순서

U1 → U2 → U3 (빌드) → U4 (파싱) → U5 → 독립 검토(가정·회귀·범위 이탈)

## 배포 참고

서버·Client를 함께 배포해야 한다. 서버 배포는 서비스 중지 → 덮어쓰기 → 시작 순서로 하고, 서버 설정에 `AllowedClients`를 먼저 추가해야 기동된다.
