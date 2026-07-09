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
                    if (data.Metadata.TryGetValue(Consts.LAST_UPDATE_TIME_KEY, out string? lastUpdateTimeStr) && long.TryParse(lastUpdateTimeStr, out long lastUpdateTimeMillis))
                    {
                        DateTime lastDateTime = lastUpdateTimeMillis.FromUnixTimeMilliseconds(DateTimeKind.Local);
                        DateTime expirationTime;
                        if (data.TtlInSeconds.HasValue)
                            expirationTime = lastDateTime.Next(TimeGranularityUnit.SECONDS, (int) data.TtlInSeconds.Value);
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

            {
                DnsAaaaRecordCollection aaaaRecords = dnsZone.GetDnsAaaaRecords();
                foreach (DnsAaaaRecordResource record in aaaaRecords)
                {
                    DnsAaaaRecordData data = record.Data;
                    if (data.Metadata.TryGetValue(Consts.LAST_UPDATE_TIME_KEY, out string? lastUpdateTimeStr) && long.TryParse(lastUpdateTimeStr, out long lastUpdateTimeMillis))
                    {
                        DateTime lastDateTime = lastUpdateTimeMillis.FromUnixTimeMilliseconds(DateTimeKind.Local);
                        DateTime expirationTime;
                        if (data.TtlInSeconds.HasValue)
                            expirationTime = lastDateTime.Next(TimeGranularityUnit.SECONDS, (int)data.TtlInSeconds.Value);
                        else
                            expirationTime = DateTime.MaxValue;
                        if (DateTime.Now > expirationTime)
                        {
                            await record.DeleteAsync(Azure.WaitUntil.Completed, cancellationToken: cancellationToken);
                            logger.Information($"AAAA Record : '{data.Name}' Expired.");
                        }
                    }
                }
            }
        }
    }
}
