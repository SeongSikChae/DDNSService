---
status: done
# 2026-11-03 U2 수정: 설계 D7(OpenExistingOnly 제거) 반영
category: security
feature: client-cert-thumbprint
phase: 개발
---

# 클라이언트 인증서 저장소 지문 사용 — 개발

선행: [0004-client-cert-thumbprint-방안.md](0004-client-cert-thumbprint-방안.md), [0005-client-cert-thumbprint-설계.md](0005-client-cert-thumbprint-설계.md)

## 확인 사항 (개발 단계 조사)

- 참조처: `ClientCertificate`/`ClientCertificatePassword`는 `DDNSService.Client/Configuration.cs:14,17`, `DDNSService.Client/Program.cs:43-46`에서만 쓰인다. 문서는 `DDNSService.Client/ReadMe.md:60-61,65,84`, `README.md:117-118,129`.
- `DynamicDnsSyncTask`는 DI의 `X509Certificate2`만 받으므로 변경 없음.
- `System.Security.Cryptography.X509Certificates`는 `Program.cs`에 이미 `using` 되어 있다.

## 작업 단위

### U1. 설정 모델

1. **대상**: `DDNSService.Client/Configuration.cs` — `Configuration`
2. **코드 초안**:
   ```csharp
   [Property(PropertyType.STRING, required: true)]
   public string Address { get; set; } = null!;

   [Property(PropertyType.STRING, required: true)]
   public string ClientCertificateThumbprint { get; set; } = null!;
   // ClientCertificate, ClientCertificatePassword 삭제
   ```
3. **검증**: U2와 함께 `dotnet build` (U1 단독으로는 `Program.cs` 참조로 빌드 실패하므로 U2와 같은 빌드로 확인)
4. **선행 단위**: 없음

### U2. 저장소에서 인증서 조회

1. **대상**: `DDNSService.Client/Program.cs` — `CreateHostApplication`
2. **코드 초안**:
   ```csharp
   private static IHost CreateHostApplication(string[] args, CmdMain cmdMain, Configuration configuration)
   {
       X509Certificate2 certificate;
       using (X509Store store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
       {
           store.Open(OpenFlags.ReadOnly);  // OpenExistingOnly 미사용 (설계 D7)
           certificate = store.Certificates
               .Find(X509FindType.FindByThumbprint, configuration.ClientCertificateThumbprint, false)
               .FirstOrDefault(c => c.HasPrivateKey)
               ?? throw new InvalidOperationException($"CurrentUser\\My 저장소에서 개인키를 가진 인증서를 찾을 수 없습니다. Thumbprint={configuration.ClientCertificateThumbprint}");
       }

       HostApplicationBuilder builder = ...  // ... existing ...
   ```
3. **검증**:
   - `dotnet build DDNSService.Client/DDNSService.Client.csproj` 종료 코드 0, 경고 증가 없음
   - 기동(정상): CurrentUser\My에 있는 개인키 포함 인증서 지문으로 스크래치패드 설정 파일을 만들어 실행 → 예외 없이 호스트 시작, `DynamicDnsSyncTask` 실행 로그(서버 응답 또는 접속 오류 로그) 확인 후 Ctrl+C로 종료. 서버 접속 성공 여부는 환경 의존이므로 "인증서 로드 이후 단계 진입"을 단언으로 삼는다.
   - 기동(실패): 존재하지 않는 지문(`0000000000000000000000000000000000000000`)으로 실행 → `InvalidOperationException`과 지문이 포함된 메시지, 비정상 종료 코드
4. **선행 단위**: U1

### U3. 문서 갱신

1. **대상**: `DDNSService.Client/ReadMe.md`, `README.md`
2. **코드 초안**:
   ```markdown
   | `ClientCertificateThumbprint` | string | 예 | 클라이언트 인증서 지문. `CurrentUser\My` 저장소에서 개인키가 있는 인증서를 찾음 |

   - 시작 시 `CurrentUser\My` 저장소에서 `ClientCertificateThumbprint`로 개인키가 있는 인증서를 찾습니다. 없으면 기동을 중단합니다.
   ```
   ```yaml
   ClientCertificateThumbprint: 0123456789ABCDEF0123456789ABCDEF01234567
   ```
   - `ReadMe.md:58` `ContentRootPath` 설명에서 "(인증서 경로 기준)" 제거
   - `ReadMe.md:84` "로드한 클라이언트 인증서" → "저장소에서 찾은 클라이언트 인증서"
   - `README.md:129` "`client.pfx` 인증서를 로드하고" → "`CurrentUser\My` 저장소에서 지문으로 인증서를 찾고"
3. **검증**: `ClientCertificate`/`ClientCertificatePassword`/`client.pfx` 검색 결과가 docs/security 이력 문서 외에 남지 않음
4. **선행 단위**: U2

## 순서

U1 → U2 (빌드·기동 검증) → U3 → 독립 검토(가정·회귀·범위 이탈)
