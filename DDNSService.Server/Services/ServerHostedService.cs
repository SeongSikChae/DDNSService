using DDNSService.Server.Tasks;

namespace DDNSService.Server.Services
{
    public class ServerHostedService(IServiceProvider serviceProvider) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            Configuration configuration = serviceProvider.GetRequiredService<Configuration>();
            ITaskScheduler taskScheduler = serviceProvider.GetRequiredService<ITaskScheduler>();
            RecordExpirationTask task = serviceProvider.GetRequiredService<RecordExpirationTask>();
            taskScheduler.AddTask(RecordExpirationTask.TASK_ID, task, new Quartz.CronExpression("0 0/30 * * * ?"));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            ITaskScheduler taskScheduler = serviceProvider.GetRequiredService<ITaskScheduler>();
            taskScheduler.RemoveTask(RecordExpirationTask.TASK_ID);
            taskScheduler.WaitForShutdown();
            return Task.CompletedTask;
        }
    }
}
