using CommandLine;
using DDNSService.Client.Services;
using DDNSService.Client.Tasks;
using DDNSService.Lib.Protos;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Configuration;
using System.Configuration;
using System.Security.Cryptography.X509Certificates;

namespace DDNSService.Client
{
    internal class Program
    {
        public sealed class CmdMain
        {
            [Option("config", Required = true, HelpText = "config file path")]
            public string ConfigFilePath { get; set; } = null!;

            [Option("log", Required = true, HelpText = "log dir path")]
            public string LogDirPath { get; set; } = null!;
        }

        static async Task Main(string[] args)
        {
            ParserResult<CmdMain> result = await Parser.Default.ParseArguments<CmdMain>(args)
                .WithParsedAsync(async cmdMain =>
                {
                    YamlDotNet.Serialization.Deserializer deserializer = new YamlDotNet.Serialization.Deserializer();
                    FileInfo configFileInfo = new FileInfo(cmdMain.ConfigFilePath);
                    string str = File.ReadAllText(configFileInfo.FullName);
                    Configuration? configuration = deserializer.Deserialize<Configuration>(str);
                    ConfigurationValidator.Validate(configuration);
                    IHost app = CreateHostApplication(args, cmdMain, configuration);
                    await app.RunAsync();
                });
        }

        private static IHost CreateHostApplication(string[] args, CmdMain cmdMain, Configuration configuration)
        {
            X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile(
                new FileInfo(Path.Combine(configuration.ContentRootPath, configuration.ClientCertificate)).FullName,
                configuration.ClientCertificatePassword
            );

            HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = args,
                ContentRootPath = configuration.ContentRootPath,
            });

            builder.Logging.Services.AddSerilog(configureLogger =>
            {
                configureLogger
                    .MinimumLevel.Information()
                    .Enrich.WithCaller()
                    .WriteTo.Console(
                        Serilog.Events.LogEventLevel.Information,
                        CallerEnricherOutputTemplate.Default
                    )
                    .WriteTo.File(
                        new DirectoryInfo(cmdMain.LogDirPath).FullName,
                        "DDNSService.Client.log",
                        Serilog.Events.LogEventLevel.Information,
                        CallerEnricherOutputTemplate.Default,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 12
                    );
            });

            builder.Services.AddSystemd();
            builder.Services.AddWindowsService();
            builder.Services.AddSingleton(configuration);
            builder.Services.AddSingleton(certificate);

            HttpClientHandler clientHandler = new HttpClientHandler();
            clientHandler.ClientCertificates.Add(certificate);

            GrpcChannel channel = GrpcChannel.ForAddress(
                configuration.Address,
                new GrpcChannelOptions
                {
                    HttpHandler = clientHandler
                }
            );

            DynamicDnsService.DynamicDnsServiceClient client = new DynamicDnsService.DynamicDnsServiceClient(channel);
            builder.Services.AddSingleton(client);
            builder.Services.AddSingleton(channel);

            builder.Services.AddSingleton<ITaskScheduler, DefaultTaskScheduler>();
            builder.Services.AddSingleton<DynamicDnsSyncTask>();
            builder.Services.AddHostedService<ClientHostedService>();

            return builder.Build();
        }
    }
}
