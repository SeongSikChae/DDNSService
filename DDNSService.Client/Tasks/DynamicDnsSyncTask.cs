using DDNSService.Lib.Protos;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography.X509Certificates;

namespace DDNSService.Client.Tasks
{
    internal class DynamicDnsSyncTask(X509Certificate2 certificate, DynamicDnsService.DynamicDnsServiceClient client, ILogger<DynamicDnsSyncTask> logger) : AsyncTask
    {
        public const string TASK_ID = "DynamicDnsSyncTask";

        public override void Dispose()
        {
        }

        public override async Task RunAsync(CancellationToken cancellationToken)
        {
            UpdateRequestProto req = new UpdateRequestProto
            {
                Id = certificate.GetNameInfo(X509NameType.EmailName, false),
                Name = certificate.GetNameInfo(X509NameType.SimpleName, false)
            };

            UpdateResponseProto res = await client.UpdateAsync(req, cancellationToken: cancellationToken);
            if (res.Error)
                logger.Error($"{res.Message}");
            else
                logger.Information($"{res.Message}");
        }
    }
}
