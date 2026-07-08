using DDNSService.Client.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DDNSService.Client.Services
{
    public sealed class ClientHostedService(IServiceProvider serviceProvider, ILogger<ClientHostedService> logger) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            Configuration configuration = serviceProvider.GetRequiredService<Configuration>();
            ITaskScheduler taskScheduler = serviceProvider.GetRequiredService<ITaskScheduler>();
            DynamicDnsSyncTask task = serviceProvider.GetRequiredService<DynamicDnsSyncTask>();

            _ = InitalizeTask(task, taskScheduler, cancellationToken);

            return Task.CompletedTask;
        }

        private async Task InitalizeTask(DynamicDnsSyncTask task, ITaskScheduler taskScheduler, CancellationToken cancellationToken)
        {
            try
            {
                await task.RunAsync(cancellationToken);
            } 
            catch (Exception e)
            {
                logger.Error(e.Message, e);
            }
            finally
            {
                taskScheduler.AddTask(DynamicDnsSyncTask.TASK_ID, task, new Quartz.CronExpression("0 0/30 * * * ?"));
            }
            
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            ITaskScheduler taskScheduler = serviceProvider.GetRequiredService<ITaskScheduler>();
            taskScheduler.RemoveTask(DynamicDnsSyncTask.TASK_ID);
            taskScheduler.WaitForShutdown();
            return Task.CompletedTask;
        }
    }
}
