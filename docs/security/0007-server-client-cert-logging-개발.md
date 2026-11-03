---
status: done
category: security
feature: server-client-cert-logging
phase: 개발
---

# 서버 클라이언트 인증서 검증 실패 로깅 — 개발

## 배경

서버(Kestrel, `ClientCertificateMode.RequireCertificate`)는 클라이언트 인증서 검증에 실패하면 TLS 핸드셰이크 직후 연결을 끊는다. Kestrel 기본 검증은 거부 이유를 로그에 남기지 않아, 클라이언트 쪽에서는 `HTTP/2 handshake` 실패만 보이고 원인을 알 수 없다.

## 확인 사항

- `KestrelServerOptions.ApplicationServices`로 콜백 실행 시점에 로거를 얻을 수 있다.
- Kestrel 기본 동작(`ClientCertificateValidation` 미지정)은 `SslPolicyErrors.None`일 때만 통과시킨다. 콜백이 같은 조건을 반환하면 판정은 바뀌지 않는다.

## 작업 단위

### U1. 클라이언트 인증서 검증 실패 로그

1. **대상**: `DDNSService.Server/Program.cs` — `CreateWebApplication`, `UseHttps` 옵션
2. **코드**:
   ```csharp
   httpsOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
   httpsOptions.ClientCertificateValidation = (clientCertificate, chain, errors) =>
   {
       if (errors != SslPolicyErrors.None)
       {
           ILogger<Program> logger = options.ApplicationServices.GetRequiredService<ILogger<Program>>();
           logger.LogWarning(
               "클라이언트 인증서 검증 실패: Subject={Subject}, Thumbprint={Thumbprint}, Errors={Errors}, ChainStatus={ChainStatus}",
               clientCertificate.Subject,
               clientCertificate.Thumbprint,
               errors,
               chain is null ? "" : string.Join(", ", chain.ChainStatus.Select(status => $"{status.Status}:{status.StatusInformation.Trim()}"))
           );
       }

       return errors == SslPolicyErrors.None;
   };
   ```
   판정은 기본 동작(`errors == None`일 때만 통과)과 같고, 실패 시 Warning 로그만 추가된다. 기존 Serilog 설정(`Information`)으로 출력된다.
3. **검증**: `dotnet build` 종료 코드 0, 검증 실패 시 서버 로그에 Warning 출력
4. **선행 단위**: 없음

인증서를 아예 보내지 않은 연결처럼 콜백까지 오지 않는 거부는 이 로그에 남지 않는다.

## 운영 참고

### 로그 해석

| `ChainStatus` | 의미 | 조치 |
| --- | --- | --- |
| `PartialChain` | 서버가 클라이언트 인증서의 발급자 체인을 루트까지 만들지 못함 | 서버 시스템 신뢰 저장소에 클라이언트 인증서의 루트·중간 CA 인증서 등록 |
| `UntrustedRoot` | 체인은 만들었으나 루트를 신뢰하지 않음 | 루트 CA 인증서를 신뢰 저장소에 등록 |
| `NotTimeValid` | 인증서 유효기간 밖 | 클라이언트 인증서 재발급 |

신뢰 저장소 등록 (RHEL 계열):

```bash
sudo install -o root -g root -m 644 <root-ca>.crt <intermediate-ca>.crt /etc/pki/ca-trust/source/anchors/
sudo update-ca-trust extract
```

서비스 재시작 없이 다음 연결부터 적용된다.

### 배포

서비스 중지 → 기존 파일 위에 덮어쓰기 → 서비스 시작 순서로 한다. 덮어쓰기는 기존 파일의 SELinux 레이블을 유지한다.

```bash
sudo systemctl stop <service>
pscp <publish 파일들> <user>@<host>:<배포 경로>/
sudo systemctl start <service>
```

서비스 실행 중에는 바이너리를 덮어쓸 수 없다(`Text file busy`). 다른 위치에 올린 뒤 `mv`로 교체하면 새 파일의 레이블(`default_t`)이 따라와, SELinux Enforcing 환경에서 systemd가 `203/EXEC Permission denied`로 기동하지 못한다. 이 경우 `chcon -t bin_t <바이너리>`로 복구한다.
