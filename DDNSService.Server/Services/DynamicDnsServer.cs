using Azure.ResourceManager;
using Azure.ResourceManager.Dns;
using Azure.ResourceManager.Resources;
using DDNSService.Lib.Protos;
using Grpc.Core;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace DDNSService.Server.Services
{
    public sealed class DynamicDnsServer(Configuration configuration, ArmClient client, ILogger<DynamicDnsServer> logger) : DynamicDnsService.DynamicDnsServiceBase
    {
        private static readonly Regex RecordNameRegex = new Regex("^[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?$", RegexOptions.Compiled);

        public override async Task<UpdateResponseProto> Update(UpdateRequestProto request, ServerCallContext context)
        {
            HttpContext httpContext = context.GetHttpContext();
            X509Certificate2 clientCertificate = httpContext.Connection.ClientCertificate!;
            string name = request.Name;

            if (!RecordNameRegex.IsMatch(name))
            {
                logger.Warning($"REJECTED Invalid name. Thumbprint: '{clientCertificate.Thumbprint}', Name: '{name}'");
                return new UpdateResponseProto
                {
                    Error = true,
                    Message = $"'{name}' 은(는) 올바른 레코드 이름이 아닙니다."
                };
            }

            bool allowed = configuration.AllowedClients.Any(allowedClient =>
                string.Equals(allowedClient.Thumbprint, clientCertificate.Thumbprint, StringComparison.OrdinalIgnoreCase)
                && allowedClient.RecordNames.Contains(name, StringComparer.OrdinalIgnoreCase));
            if (!allowed)
            {
                logger.Warning($"REJECTED Not allowed. Thumbprint: '{clientCertificate.Thumbprint}', Name: '{name}'");
                return new UpdateResponseProto
                {
                    Error = true,
                    Message = $"'{name}' 에 대한 갱신 권한이 없습니다."
                };
            }

            SubscriptionResource subscription = await client.GetSubscriptionResource(new Azure.Core.ResourceIdentifier(configuration.AzureDns.ResourceId)).GetAsync(context.CancellationToken);
            ResourceGroupResource resourceGroup = await subscription.GetResourceGroupAsync(configuration.AzureDns.ResourceGroupName, context.CancellationToken);
            DnsZoneResource dnsZone = await resourceGroup.GetDnsZoneAsync(configuration.AzureDns.DnsZoneName, context.CancellationToken);

            IPAddress address = httpContext.Connection.RemoteIpAddress!;
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            switch (address.AddressFamily)
            {
                case System.Net.Sockets.AddressFamily.InterNetwork:
                    logger.Information($"REQUEST Name: '{name}', Address: '{address}', Type: A");

                    {
                        DnsARecordCollection aRecords = dnsZone.GetDnsARecords();
                        DnsARecordData data;
                        if (await aRecords.ExistsAsync(name, context.CancellationToken))
                        {
                            DnsARecordResource aRecord = await aRecords.GetAsync(name, context.CancellationToken);
                            data = aRecord.Data;
                            data.DnsARecords.Clear();
                        }
                        else
                        {
                            data = new DnsARecordData
                            {
                                TtlInSeconds = 300
                            };
                        }

                        data.DnsARecords.Add(new Azure.ResourceManager.Dns.Models.DnsARecordInfo
                        {
                            IPv4Address = address
                        });
                        if (data.Metadata.TryGetValue(Consts.LAST_UPDATE_TIME_KEY, out string? _))
                            data.Metadata[Consts.LAST_UPDATE_TIME_KEY] = $"{DateTime.Now.ToMilliseconds()}";
                        else
                            data.Metadata.Add(Consts.LAST_UPDATE_TIME_KEY, $"{DateTime.Now.ToMilliseconds()}");
                        if (!data.Metadata.TryGetValue(Consts.EXPIRATION_KEY, out string? _))
                            data.Metadata.Add(Consts.EXPIRATION_KEY, "3600");
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

                    {
                        DnsAaaaRecordCollection aaaaRecords = dnsZone.GetDnsAaaaRecords();
                        DnsAaaaRecordData data;
                        if (await aaaaRecords.ExistsAsync(name, context.CancellationToken))
                        {
                            DnsAaaaRecordResource aRecord = await aaaaRecords.GetAsync(name, context.CancellationToken);
                            data = aRecord.Data;
                            data.DnsAaaaRecords.Clear();
                        }
                        else
                        {
                            data = new DnsAaaaRecordData
                            {
                                TtlInSeconds = 300
                            };
                        }

                        data.DnsAaaaRecords.Add(new Azure.ResourceManager.Dns.Models.DnsAaaaRecordInfo
                        {
                            IPv6Address = address
                        });
                        if (data.Metadata.TryGetValue(Consts.LAST_UPDATE_TIME_KEY, out string? _))
                            data.Metadata[Consts.LAST_UPDATE_TIME_KEY] = $"{DateTime.Now.ToMilliseconds()}";
                        else
                            data.Metadata.Add(Consts.LAST_UPDATE_TIME_KEY, $"{DateTime.Now.ToMilliseconds()}");
                        if (!data.Metadata.TryGetValue(Consts.EXPIRATION_KEY, out string? _))
                            data.Metadata.Add(Consts.EXPIRATION_KEY, "3600");
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
