---
status: done
category: security
feature: record-prefix
phase: 설계
---

# 등록 레코드 이름 설정화 — 설계

선행: [0008-record-prefix-방안.md](0008-record-prefix-방안.md)

## 구조

```text
Client
 └─ DynamicDnsSyncTask.RunAsync
      └─ UpdateRequestProto { Id = 인증서 EmailName (기존), Name = Configuration.RecordName (변경) }

Server
 └─ DynamicDnsServer.Update
      ├─ clientCertificate = HttpContext.Connection.ClientCertificate   (RequireCertificate라 항상 존재)
      ├─ prefix 레이블 검증 (실패 → Error 응답)
      ├─ AllowedClients에서 Thumbprint 일치 항목의 RecordNames에 prefix 포함 여부 (없으면 → Error 응답)
      └─ name = prefix → 기존 A/AAAA 갱신 흐름 그대로
```

새 클래스는 서버 설정 항목 `AllowedClientConfiguration` 하나만 추가한다. 검증 로직은 `DynamicDnsServer.Update` 안에 둔다.

## 설정 데이터

### Client (`DDNSService.Client/Configuration.cs`)

| 변경 | 키 | 타입 | 필수 | 설명 |
| --- | --- | --- | --- | --- |
| 추가 | `RecordName` | string | 예 | 등록할 레코드 이름 prefix. 예: `home` → `home.<영역>` |

```yaml
ContentRootPath: /opt/ddns/client
Address: https://ddns.example.com:5001
ClientCertificateThumbprint: 0123456789ABCDEF0123456789ABCDEF01234567
RecordName: home
```

### Server (`DDNSService.Server/Configuration.cs`)

| 변경 | 키 | 타입 | 필수 | 설명 |
| --- | --- | --- | --- | --- |
| 추가 | `AllowedClients` | list | 예 | 클라이언트별 허용 레코드 목록 |
| 추가 | `AllowedClients[].Thumbprint` | string | 예 | 클라이언트 인증서 지문 |
| 추가 | `AllowedClients[].RecordNames` | string list | 예 | 이 인증서가 갱신할 수 있는 prefix 목록 (1개 이상) |

```yaml
AllowedClients:
  - Thumbprint: 0123456789ABCDEF0123456789ABCDEF01234567
    RecordNames:
      - home
      - nas
```

`Configuration.Validate()`에서 `AllowedClients`가 null이 아닌지, 각 항목의 `Thumbprint`가 비어 있지 않고 `RecordNames`가 1개 이상인지 검사한다. 비어 있는 목록(`[]`)은 허용하되, 그 경우 모든 요청이 거부된다.

## 프로토콜

`dynamic-dns.proto`는 필드 변경 없음. `UpdateRequestProto.name`의 의미만 "전체 DNS 이름" → "레코드 이름 prefix"로 바뀐다. `id`는 기존대로 보내고 서버는 쓰지 않는다.

## 서버 처리 규칙

1. **레이블 검증**: `^[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?$`. 점(`.`), `@`, `*`, 빈 값, 63자 초과 거부.
2. **권한 확인**: `AllowedClients` 중 `Thumbprint`가 클라이언트 인증서 지문과 같고(대소문자 무시) `RecordNames`에 prefix가 있는(대소문자 무시) 항목이 있어야 한다.
3. 거부 시 `UpdateResponseProto { Error = true, Message = ... }`로 응답하고 서버 로그에 Warning(지문, 요청 prefix)을 남긴다. gRPC 상태 코드는 쓰지 않는다(기존 오류 응답 방식 유지).
4. 허용 시 `name = prefix`로 기존 A/AAAA 갱신 코드를 그대로 쓴다. 기존 `EndsWith(DnsZoneName)` 검사와 `Replace` 제거.

## 결정 목록

| # | 결정 | 근거 |
| --- | --- | --- |
| D1 | 클라이언트 식별자는 인증서 **지문** | 인증서마다 고유. Subject/Email은 같은 값으로 다른 인증서를 발급할 수 있음. 인증서 갱신 시 서버 설정의 지문도 바꿔야 함 |
| D2 | Client 설정 키 이름 `RecordName` | Azure DNS의 레코드 집합 이름(relative name)과 같은 개념 |
| D3 | 허용 목록은 지문 1개당 prefix 여러 개 | 한 장비가 여러 이름을 쓸 수 있게 |
| D4 | 권한 판단은 TLS 인증서 기준, 요청 `id`는 무시 | 요청 본문은 클라이언트가 임의로 바꿀 수 있음 |
| D5 | 오류는 기존처럼 `Error = true` 응답 | Client의 기존 오류 로그 처리와 호환 |
| D6 | 서버 설정 변경 반영은 재기동 필요 | 기존 설정 로딩 방식과 동일, 핫 리로드는 비범위 |

## 문서 영향

- `DDNSService.Client/ReadMe.md`: 설정 표에 `RecordName`, 동작 설명(`Name` ← `RecordName`)
- `DDNSService.Server/ReadMe.md`: 설정 표에 `AllowedClients`, 권한 확인 동작
- `README.md`: 클라이언트·서버 YAML 예시, 동작 흐름
- `DDNSService.Lib/README.md`: `name` 필드 의미
