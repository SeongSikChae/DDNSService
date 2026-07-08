# DDNSService.Lib

`DDNSService.Lib`는 DDNSService 솔루션의 **공유 라이브러리**로, 클라이언트(`DDNSService.Client`)와 서버(`DDNSService.Server`)가 함께 사용하는 gRPC 통신 계약과 공통 의존성을 한곳에서 관리합니다.

## 개요

이 프로젝트는 실행 가능한 애플리케이션이 아니라, 아래 두 프로젝트에서 `ProjectReference`로 참조되는 라이브러리입니다.

- `DDNSService.Client` — 자신의 공인 IP를 서버로 전송하는 클라이언트
- `DDNSService.Server` — 전달받은 IP를 Azure DNS Zone에 A/AAAA 레코드로 반영하는 서버

라이브러리가 담당하는 핵심 역할은 다음과 같습니다.

1. **gRPC 서비스 계약 정의** — `.proto` 파일로 클라이언트/서버가 공유하는 메시지와 RPC를 정의합니다.
2. **공통 의존성 집약** — 로깅(Serilog), 설정(Configuration), 호스팅(Windows Service / systemd), 명령줄 파싱, YAML 처리 등 양쪽에서 공통으로 필요한 NuGet 패키지를 이 라이브러리에 모아 전이(transitive) 참조로 제공합니다.

## 대상 프레임워크

- **.NET 10.0** (`net10.0`)
- `ImplicitUsings` 활성화
- `Nullable` 참조 형식 활성화

## gRPC 서비스 계약

`Protos/dynamic-dns.proto`에 통신 규약이 정의되어 있습니다.

```proto
syntax = "proto3";

option csharp_namespace = "DDNSService.Lib.Protos";

message UpdateRequestProto {
  string id = 1;
  string name = 2;
}

message UpdateResponseProto {
  bool error = 1;
  string message = 2;
}

service DynamicDnsService {
  rpc Update(UpdateRequestProto) returns (UpdateResponseProto);
}
```

### 메시지

| 메시지 | 필드 | 설명 |
| --- | --- | --- |
| `UpdateRequestProto` | `id` | 요청 식별자 (클라이언트는 인증서의 Email 정보를 사용) |
| | `name` | 갱신할 DNS 이름 (클라이언트는 인증서의 Simple Name을 사용) |
| `UpdateResponseProto` | `error` | 처리 실패 여부 (`true`면 실패) |
| | `message` | 처리 결과 메시지 |

### 서비스

- `DynamicDnsService.Update` — 클라이언트가 전달한 이름/식별자를 바탕으로 서버가 호출자의 원격 IP를 Azure DNS 레코드에 반영합니다.

### 코드 생성

`.csproj`에서 `Grpc.Tools`가 빌드 시 `.proto`로부터 C# 코드를 자동 생성합니다.

```xml
<ItemGroup>
  <Protobuf Include="Protos\**\*.proto" GrpcServices="Both" />
</ItemGroup>
```

- `GrpcServices="Both"`이므로 **서버 베이스 클래스**(`DynamicDnsService.DynamicDnsServiceBase`)와 **클라이언트 스텁**(`DynamicDnsService.DynamicDnsServiceClient`)이 모두 생성됩니다.
- 생성 코드는 `DDNSService.Lib.Protos` 네임스페이스에 배치됩니다.

## 사용 예시

### 서버 (구현)

```csharp
using DDNSService.Lib.Protos;

public sealed class DynamicDnsServer : DynamicDnsService.DynamicDnsServiceBase
{
    public override async Task<UpdateResponseProto> Update(
        UpdateRequestProto request, ServerCallContext context)
    {
        // request.Name / request.Id 를 사용해 DNS 레코드 반영
        return new UpdateResponseProto { Error = false, Message = "반영 완료" };
    }
}
```

### 클라이언트 (호출)

```csharp
using DDNSService.Lib.Protos;

UpdateRequestProto req = new UpdateRequestProto
{
    Id = certificate.GetNameInfo(X509NameType.EmailName, false),
    Name = certificate.GetNameInfo(X509NameType.SimpleName, false)
};

UpdateResponseProto res = await client.UpdateAsync(req, cancellationToken: cancellationToken);
```

## 주요 의존성

| 패키지 | 용도 |
| --- | --- |
| `Google.Protobuf`, `Grpc.Net.Client`, `Grpc.Tools` | gRPC 계약 정의 및 클라이언트/서버 코드 생성 |
| `Biz.Bizadm.Serilog.Enrichers.Caller` | Serilog 로그에 호출자 정보 보강 |
| `Biz.Bizadm.Serilog.Sinks.File.Extensions` | Serilog 파일 싱크 확장 |
| `Biz.Bizadm.System.Configuration.Extensions` | 설정 처리 확장 |
| `Biz.Bizadm.System.Threading.Extensions` | 스레딩/비동기 작업 확장 |
| `CommandLineParser` | 명령줄 인수 파싱 |
| `YamlDotNet` | YAML 설정 파일 처리 |
| `Serilog.Extensions.Hosting`, `Serilog.Sinks.Console` | 호스팅 환경 로깅 및 콘솔 출력 |
| `Microsoft.Extensions.Hosting.WindowsServices` | Windows 서비스로 호스팅 |
| `Microsoft.Extensions.Hosting.Systemd` | Linux systemd 서비스로 호스팅 |

> 위 패키지들은 이 라이브러리를 참조하는 클라이언트/서버 프로젝트에 전이적으로 제공되어, 각 프로젝트가 동일한 로깅·설정·호스팅 기반을 공유하도록 합니다.

## 프로젝트 구조

```
DDNSService.Lib/
├── Protos/
│   └── dynamic-dns.proto     # gRPC 서비스 및 메시지 정의
├── DDNSService.Lib.csproj    # 대상 프레임워크, 패키지, Protobuf 빌드 설정
└── README.md
```

## 빌드

이 라이브러리는 단독 실행 대상이 아니며, 솔루션 빌드 시 함께 컴파일됩니다.

```bash
dotnet build DDNSService.Lib/DDNSService.Lib.csproj
```

빌드 과정에서 `Grpc.Tools`가 `.proto` 파일을 컴파일하여 gRPC C# 코드를 생성합니다.
