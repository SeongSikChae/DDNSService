using Azure.ResourceManager;
using Azure.ResourceManager.Dns;
using Azure.ResourceManager.Resources;

namespace DDNSService.Server.Tasks
{
    public class RecordExpirationTask(Configuration configuration, ArmClient client, ILogger<RecordExpirationTask> logger) : AsyncTask
    {
        public const string TASK_ID = "RecordExpirationTask";

        public override void Dispose()
        {
        }

        public override async Task RunAsync(CancellationToken cancellationToken)
        {
            SubscriptionResource subscription = await client.GetSubscriptionResource(new Azure.Core.ResourceIdentifier(configuration.ResourceId)).GetAsync(cancellationToken);
            ResourceGroupResource resourceGroup = await subscription.GetResourceGroupAsync(configuration.ResourceGroupName, cancellationToken);
            DnsZoneResource dnsZone = await resourceGroup.GetDnsZoneAsync(configuration.DnsZoneName, cancellationToken);

            {
                DnsARecordCollection aRecords = dnsZone.GetDnsARecords();
                foreach (DnsARecordResource record in aRecords)
                {
                    DnsARecordData data = record.Data;
                    long? lastUpdateTimeMillis = null;
                    if (data.Metadata.TryGetValue(Consts.LAST_UPDATE_TIME_KEY, out string? lastUpdateTimeStr))
                    {
                        if (long.TryParse(lastUpdateTimeStr!, out var r))
                            lastUpdateTimeMillis = r;
                    }

                    int? expirationInSeconds = null;
                    if (data.Metadata.TryGetValue(Consts.EXPIRATION_KEY, out string? expirationStr))
                    {
                        if (int.TryParse(expirationStr, out var r))
                            expirationInSeconds = r;
                    }

                    DateTime lastUpdateTime = (lastUpdateTimeMillis ?? DateTime.MinValue.ToMilliseconds()).FromUnixTimeMilliseconds(DateTimeKind.Local);
                    DateTime expirationTime;
                    if (expirationInSeconds.HasValue)
                        expirationTime = lastUpdateTime.Next(TimeGranularityUnit.SECONDS, expirationInSeconds.Value);
                    else
                        expirationTime = DateTime.MaxValue;

                    if (DateTime.Now > expirationTime)
                    {
                        await record.DeleteAsync(Azure.WaitUntil.Completed, cancellationToken: cancellationToken);
                        logger.Information($"A Record : '{data.Name}' Expired.");
                    }                    
                }
            }

            {
                DnsAaaaRecordCollection aaaaRecords = dnsZone.GetDnsAaaaRecords();
                foreach (DnsAaaaRecordResource record in aaaaRecords)
                {
                    DnsAaaaRecordData data = record.Data;
                    long? lastUpdateTimeMillis = null;
                    if (data.Metadata.TryGetValue(Consts.LAST_UPDATE_TIME_KEY, out string? lastUpdateTimeStr))
                    {
                        if (long.TryParse(lastUpdateTimeStr!, out var r))
                            lastUpdateTimeMillis = r;
                    }

                    int? expirationInSeconds = null;
                    if (data.Metadata.TryGetValue(Consts.EXPIRATION_KEY, out string? expirationStr))
                    {
                        if (int.TryParse(expirationStr, out var r))
                            expirationInSeconds = r;
                    }

                    DateTime lastUpdateTime = (lastUpdateTimeMillis ?? DateTime.MinValue.ToMilliseconds()).FromUnixTimeMilliseconds(DateTimeKind.Local);
                    DateTime expirationTime;
                    if (expirationInSeconds.HasValue)
                        expirationTime = lastUpdateTime.Next(TimeGranularityUnit.SECONDS, expirationInSeconds.Value);
                    else
                        expirationTime = DateTime.MaxValue;

                    if (DateTime.Now > expirationTime)
                    {
                        await record.DeleteAsync(Azure.WaitUntil.Completed, cancellationToken: cancellationToken);
                        logger.Information($"A Record : '{data.Name}' Expired.");
                    }
                }
            }
        }
    }
}
