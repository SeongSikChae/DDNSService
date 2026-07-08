using Azure.ResourceManager;
using Azure.ResourceManager.Dns;
using Azure.ResourceManager.Resources;
using DDNSService.Lib.Protos;
using Grpc.Core;
using System.Net;

namespace DDNSService.Server.Services
{
    public sealed class DynamicDnsServer(Configuration configuration, ArmClient client, ILogger<DynamicDnsServer> logger) : DynamicDnsService.DynamicDnsServiceBase
    {
        public override async Task<UpdateResponseProto> Update(UpdateRequestProto request, ServerCallContext context)
        {
            if (!request.Name.EndsWith(configuration.DnsZoneName)) {
                return new UpdateResponseProto
                {
                    Error = true,
                    Message = $"'{configuration.DnsZoneName}' 영역 내 만 지원합니다."
                };
            }

            string name = request.Name.Replace($".{configuration.DnsZoneName}", string.Empty);

            SubscriptionResource subscription = await client.GetSubscriptionResource(new Azure.Core.ResourceIdentifier(configuration.ResourceId)).GetAsync(context.CancellationToken);
            ResourceGroupResource resourceGroup = await subscription.GetResourceGroupAsync(configuration.ResourceGroupName, context.CancellationToken);
            DnsZoneResource dnsZone = await resourceGroup.GetDnsZoneAsync(configuration.DnsZoneName, context.CancellationToken);

            HttpContext httpContext = context.GetHttpContext();
            IPAddress address = httpContext.Connection.RemoteIpAddress!;
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            switch (address.AddressFamily)
            {
                case System.Net.Sockets.AddressFamily.InterNetwork:
                    logger.Information($"REQUEST Name: '{name}', Address: '{address}', Type: A");

                    DnsARecordCollection aRecords = dnsZone.GetDnsARecords();
                    if (await aRecords.ExistsAsync(name, context.CancellationToken))
                    {
                        DnsARecordResource aRecord = await aRecords.GetAsync(name, context.CancellationToken);
                        DnsARecordData data = aRecord.Data;
                        data.DnsARecords.Clear();
                        data.DnsARecords.Add(new Azure.ResourceManager.Dns.Models.DnsARecordInfo
                        {
                            IPv4Address = address
                        });
                        data.TtlInSeconds = 3600;
                        if (data.Metadata.TryGetValue("LastUpdateTime", out string? _))
                            data.Metadata["LastUpdateTime"] = $"{DateTime.Now.ToMilliseconds()}";
                        else
                            data.Metadata.Add("LastUpdateTime", $"{DateTime.Now.ToMilliseconds()}");
                        await aRecord.UpdateAsync(data, cancellationToken: context.CancellationToken);
                    }
                    else
                    {
                        DnsARecordData data = new DnsARecordData
                        {
                            TtlInSeconds = 3600
                        };

                        data.DnsARecords.Add(new Azure.ResourceManager.Dns.Models.DnsARecordInfo
                        {
                            IPv4Address = address
                        });
                        data.Metadata.Add("LastUpdateTime", $"{DateTime.Now.ToMilliseconds()}");
                        await aRecords.CreateOrUpdateAsync(Azure.WaitUntil.Completed, name, data, cancellationToken: context.CancellationToken);
                    }

                    logger.Information($"Updated. Name: '{name}', Address: '{address}', Type: A");

                    return new UpdateResponseProto
                    {
                        Error = false,
                        Message = $"'{name}' - {address} 에 대해 A Record가 반영되었습니다."
                    };
                case System.Net.Sockets.AddressFamily.InterNetworkV6:
                    logger.Information($"REQUEST Name: '{name}', Address: '{address}', Type: AAAA");

                    DnsAaaaRecordCollection aaaaRecords = dnsZone.GetDnsAaaaRecords();
                    if (await aaaaRecords.ExistsAsync(name, context.CancellationToken))
                    {
                        DnsAaaaRecordResource aaaaRecord = await aaaaRecords.GetAsync(name, context.CancellationToken);
                        DnsAaaaRecordData data = aaaaRecord.Data;
                        data.DnsAaaaRecords.Clear();
                        data.DnsAaaaRecords.Add(new Azure.ResourceManager.Dns.Models.DnsAaaaRecordInfo
                        {
                            IPv6Address = address
                        });
                        data.TtlInSeconds = 3600;
                        if (data.Metadata.TryGetValue("LastUpdateTime", out string? _))
                            data.Metadata["LastUpdateTime"] = $"{DateTime.Now.ToMilliseconds()}";
                        else
                            data.Metadata.Add("LastUpdateTime", $"{DateTime.Now.ToMilliseconds()}");
                        await aaaaRecord.UpdateAsync(data, cancellationToken: context.CancellationToken);
                    } 
                    else
                    {
                        DnsAaaaRecordData data = new DnsAaaaRecordData
                        {
                            TtlInSeconds = 3600
                        };

                        data.DnsAaaaRecords.Add(new Azure.ResourceManager.Dns.Models.DnsAaaaRecordInfo
                        {
                            IPv6Address = address
                        });
                        data.Metadata.Add("LastUpdateTime", $"{DateTime.Now.ToMilliseconds()}");
                        await aaaaRecords.CreateOrUpdateAsync(Azure.WaitUntil.Completed, name, data, cancellationToken: context.CancellationToken);
                    }

                    logger.Information($"Updated. Name: '{name}', Address: '{address}', Type: AAAA");

                    return new UpdateResponseProto
                    {
                        Error = false,
                        Message = $"'{name}' - {address} 에 대해 AAAA Record가 반영되었습니다."
                    };
            }

            return new UpdateResponseProto
            {
                Error = true,
                Message = $"'{name}' 에 대해 DNS 반영에 실패하였습니다."
            };
        }
    }
}
