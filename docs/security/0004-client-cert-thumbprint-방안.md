---
status: done
category: security
feature: client-cert-thumbprint
phase: 방안
---

# 클라이언트 인증서 저장소 지문 사용 — 방안

## 문제

`DDNSService.Client`는 설정 YAML의 `ClientCertificate`(PKCS#12 파일 경로, `ContentRootPath` 기준)와 `ClientCertificatePassword`로 클라이언트 인증서를 로드한다 (`Program.cs`의 `X509CertificateLoader.LoadPkcs12FromFile`). 비밀번호가 설정 파일에 평문으로 남고, 개인키가 담긴 PFX 파일도 별도로 배포·보관해야 한다. 운영 환경에는 개인키를 가진 인증서가 이미 인증서 저장소에 설치되어 있다.

## 목표

- 설정에서 `ClientCertificate`, `ClientCertificatePassword`를 없애고 인증서 지문(`ClientCertificateThumbprint`) 하나만 받는다.
- 기동 시 **CurrentUser\My** 저장소에서 지문으로 인증서를 찾고, 개인키가 있는 인증서를 mTLS 클라이언트 인증서로 쓴다.
- 찾은 `X509Certificate2`를 기존과 같이 DI 싱글턴과 `HttpClientHandler.ClientCertificates`에 넣는다. 이후 동작(요청 `Id`/`Name` 구성 등)은 그대로다.

## 대안

| 안 | 설명 | 장점 | 단점 |
| --- | --- | --- | --- |
| A. PFX 파일 유지 | 현행 | 변경 없음 | 비밀번호 평문, PFX 파일 관리 |
| B. 지문 단독 (저장소) | 파일·비밀번호 키 제거, 지문만 | 비밀 정보가 설정에서 사라짐, 개인키는 저장소가 보호 | 실행 계정의 저장소에 인증서 설치 필요 |
| C. 파일·지문 병행 | 둘 중 하나 선택 | 이행이 유연 | 설정·검증 분기 증가, 비밀번호 키가 남음 |

저장소 위치 후보: `CurrentUser\My`, `LocalMachine\My`, 설정 선택, 순차 검색.

## 채택 방안

**B. 지문 단독**, 저장소는 **`StoreLocation.CurrentUser` / `StoreName.My` 고정** (사용자 지정).

흐름:

1. YAML 로드 → `ConfigurationValidator.Validate` (`ClientCertificateThumbprint` 필수)
2. `X509Store(StoreName.My, StoreLocation.CurrentUser)`를 읽기 전용으로 연다.
3. `X509FindType.FindByThumbprint`로 검색, `HasPrivateKey`인 인증서를 선택한다.
4. 없으면 지문을 포함한 예외로 기동을 중단한다.
5. 이후 DI·gRPC 채널 구성은 현행 유지.

## 비범위

- `DDNSService.Server` 인증서 로딩 변경
- 저장소 위치 설정화, PFX 파일 방식 병행(하위 호환) — 기존 설정 파일은 키를 직접 바꿔야 함
- 인증서 설치·갱신 자동화

## 리스크

- **실행 계정별 저장소**: CurrentUser는 프로세스 실행 계정의 저장소다. Windows 서비스(LocalSystem 등)로 돌리면 그 계정의 CurrentUser\My에 인증서가 있어야 한다. 설치 계정과 실행 계정을 맞춰야 한다.
- **Linux**: .NET은 CurrentUser\My를 `~/.dotnet/corefx/cryptography/x509stores/my`로 매핑한다. systemd 서비스 계정 홈에 인증서(개인키 포함)를 넣어 두어야 한다.
- **지문 입력 오류**: certmgr에서 복사하면 공백이나 보이지 않는 문자(U+200E 등)가 섞일 수 있다. 정규화 여부는 설계에서 정한다.
- **개인키 접근 권한**: 인증서는 있어도 실행 계정에 개인키 읽기 권한이 없으면 TLS 핸드셰이크에서 실패한다.
