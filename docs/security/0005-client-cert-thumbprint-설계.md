---
status: done
# 2026-11-03 D7 추가: 검토 회귀(OpenExistingOnly 제거)를 사용자 결정으로 반영
category: security
feature: client-cert-thumbprint
phase: 설계
---

# 클라이언트 인증서 저장소 지문 사용 — 설계

선행: [0004-client-cert-thumbprint-방안.md](0004-client-cert-thumbprint-방안.md)

## 구조

```text
Program.Main
 ├─ YAML 역직렬화 → ConfigurationValidator.Validate      (기존, ClientCertificateThumbprint 필수 검증)
 └─ CreateHostApplication
      ├─ 인증서 조회 (변경: PFX 로드 → X509Store 조회)
      │    ├─ X509Store(StoreName.My, StoreLocation.CurrentUser).Open(ReadOnly)
      │    ├─ Certificates.Find(FindByThumbprint, thumbprint, validOnly: false)
      │    ├─ HasPrivateKey 인 첫 인증서 선택
      │    └─ 없으면 InvalidOperationException (기동 중단)
      ├─ AddSingleton(certificate)                         (기존)
      └─ HttpClientHandler.ClientCertificates.Add → GrpcChannel (기존)
```

새 클래스·헬퍼 메서드는 만들지 않는다. 조회 코드는 `CreateHostApplication` 안에서 기존 PFX 로드 자리를 대체한다.

## 설정 데이터 (Configuration.cs)

| 변경 | 키 | 타입 | 필수 |
| --- | --- | --- | --- |
| 삭제 | `ClientCertificate` | string | - |
| 삭제 | `ClientCertificatePassword` | string | - |
| 추가 | `ClientCertificateThumbprint` | string (`PropertyType.STRING`) | 예 |

```yaml
ContentRootPath: /opt/ddns/client
Address: https://ddns.example.com:5001
ClientCertificateThumbprint: 0123456789ABCDEF0123456789ABCDEF01234567
```

`ContentRootPath`는 인증서 경로 기준 용도가 사라지지만 `HostApplicationBuilderSettings.ContentRootPath`로 계속 쓰이므로 유지한다.

## 인터페이스 / 의존성

- `System.Security.Cryptography.X509Certificates`(`X509Store`, `X509FindType`, `StoreName`, `StoreLocation`, `OpenFlags`) — 이미 `using` 되어 있음, 패키지 추가 없음.
- DI에 등록되는 `X509Certificate2` 타입·수명 그대로 → `DynamicDnsSyncTask` 변경 없음.
- 저장소(`X509Store`)는 조회 후 `using`으로 닫는다. 반환된 `X509Certificate2`는 저장소를 닫아도 유효하다.

## 결정 목록

| # | 결정 | 근거 |
| --- | --- | --- |
| D1 | 저장소 `CurrentUser\My` 고정 | 방안 합의 |
| D2 | `validOnly: false` | 사설 CA 발급 인증서는 로컬 체인 검증에 실패할 수 있음. 신뢰 판단은 서버 mTLS가 함 |
| D3 | `HasPrivateKey == true`인 인증서만 사용 | mTLS 클라이언트 인증에 개인키 필수, 공개키만 있는 동일 지문 인증서 배제 |
| D4 | 지문 정규화 하지 않음 | `FindByThumbprint`는 대소문자를 구분하지 않음. 공백·숨은 문자는 설정 오류로 보고 예외 메시지에 입력값을 그대로 노출해 원인을 알 수 있게 함 (요청 밖 방어 코드 배제) |
| D5 | 못 찾으면 `InvalidOperationException("...Thumbprint=...")`로 기동 중단 | 현행도 PFX 로드 실패 시 예외로 기동 중단. 동일 처리 방식 유지 |
| D6 | 여러 개가 매칭되면 첫 번째(`HasPrivateKey`) 사용 | 같은 지문 = 같은 인증서이므로 구분 불필요 |
| D7 | 저장소는 `OpenFlags.ReadOnly`만으로 연다 (`OpenExistingOnly` 미사용) | 저장소가 없을 때(주로 Linux) `Open()`이 `CryptographicException`을 던지지 않고 빈 목록이 되어, D5의 동일 예외 메시지로 수렴 (검토 회귀 반영) |

## 문서 영향

- `DDNSService.Client/ReadMe.md`: 설정 표, 보안/통신 섹션, DI 설명("로드한" → "저장소에서 찾은")
- `README.md`: 클라이언트 YAML 예시, 동작 흐름 1번(`client.pfx` 언급)
