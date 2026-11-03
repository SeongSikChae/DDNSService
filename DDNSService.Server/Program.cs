using Azure.Identity;
using Azure.ResourceManager;
using Biz.Bizadm.KMS.Cipher;
using Biz.Bizadm.KMS.Credentials.Cipher;
using CommandLine;
using DDNSService.Server.Services;
using DDNSService.Server.Tasks;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Serilog;
using Serilog.Configuration;
using System.Configuration;
using System.Net.Security;
using System.Security.AccessControl;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using YamlDotNet.Serialization;

namespace DDNSService.Server
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
                    ProtectSecrets(configFileInfo, configuration);
                    WebApplication app = CreateWebApplication(args, cmdMain, configuration);
                    await StartAsync(app, cmdMain, configuration);
                });
        }

        private static void ProtectSecrets(FileInfo configFileInfo, Configuration configuration)
        {
            bool rewrite = false;

            if (!string.IsNullOrWhiteSpace(configuration.Kms.CredentialStore))
                Environment.SetEnvironmentVariable("GCM_CREDENTIAL_STORE", configuration.Kms.CredentialStore);

            FileInfo dekFileInfo = new FileInfo(Path.Combine(configuration.ContentRootPath, configuration.Kms.DekFilePath));

            IOsKekCredentialStore credentialStore = OsKekCredentialProvider.CreateForCurrentOs(Consts.KMS_KEK_SERVICE, Consts.KMS_KEK_ACCOUNT);
            try
            {
                CryptographicOperations.ZeroMemory(credentialStore.GetPassword());
            }
            catch (InvalidOperationException e) when (e.Message.StartsWith("KEK credential not found", StringComparison.Ordinal))
            {
                if (dekFileInfo.Exists)
                    throw new InvalidOperationException($"KEK credential not found but DEK file exists: {dekFileInfo.FullName}", e);

                byte[] password = RandomNumberGenerator.GetBytes(32);
                credentialStore.StorePassword(password);
                CryptographicOperations.ZeroMemory(password);
            }

            if (string.IsNullOrWhiteSpace(configuration.Kms.KekSalt))
            {
                configuration.Kms.KekSalt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
                WriteConfiguration(configFileInfo, configuration);
            }

            using AesGcmKekManager kekManager = AesGcmKekManager.Create(credentialStore, Convert.FromBase64String(configuration.Kms.KekSalt), configuration.Kms.KekIterations!.Value);
            using AesGcmDekCipher dekCipher = AesGcmDekCipher.Create(kekManager.Current, dekFileInfo);
            using DekCipherService dekCipherService = new DekCipherService(dekCipher, leaveOpen: true);

            string EncryptIfNeeded(string value)
            {
                if (dekCipherService.StartsWithFromPrefix(value))
                    return value;
                rewrite = true;
                return dekCipherService.Encrypt(value);
            }

            configuration.ServerCertificatePassword = EncryptIfNeeded(configuration.ServerCertificatePassword);
            configuration.AzureDns.ClientSecret = EncryptIfNeeded(configuration.AzureDns.ClientSecret);

            if (rewrite)
                WriteConfiguration(configFileInfo, configuration);

            configuration.ServerCertificatePassword = dekCipherService.Decrypt(configuration.ServerCertificatePassword);
            configuration.AzureDns.ClientSecret = dekCipherService.Decrypt(configuration.AzureDns.ClientSecret);
        }

        private static void WriteConfiguration(FileInfo configFileInfo, Configuration configuration)
        {
            ISerializer serializer = new SerializerBuilder()
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
                .Build();
            string tempFilePath = configFileInfo.FullName + ".tmp";
            try
            {
                File.WriteAllText(tempFilePath, serializer.Serialize(configuration));
                if (OperatingSystem.IsWindows())
                {
                    FileSecurity security = new FileSecurity();
                    security.SetSecurityDescriptorBinaryForm(configFileInfo.GetAccessControl().GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
                    new FileInfo(tempFilePath).SetAccessControl(security);
                }
                else
                    File.SetUnixFileMode(tempFilePath, File.GetUnixFileMode(configFileInfo.FullName));
                File.Move(tempFilePath, configFileInfo.FullName, overwrite: true);
            }
            catch
            {
                File.Delete(tempFilePath);
                throw;
            }
        }

        private static WebApplication CreateWebApplication(string[] args, CmdMain cmdMain, Configuration configuration)
        {
            X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile(
                new FileInfo(Path.Combine(configuration.ContentRootPath, configuration.ServerCertificate)).FullName,
                configuration.ServerCertificatePassword
            );

            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = configuration.ContentRootPath
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
                        "DDNSService.Server.log",
                        Serilog.Events.LogEventLevel.Information,
                        CallerEnricherOutputTemplate.Default,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 12
                    );
            });

            builder.WebHost.UseKestrel(options =>
            {
                options.ListenAnyIP(configuration.Port!.Value, configure =>
                {
                    configure.Protocols = HttpProtocols.Http2 | HttpProtocols.Http3;
                    configure.UseHttps(httpsOptions =>
                    {
                        httpsOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                        httpsOptions.ServerCertificate = certificate;

                        if (configuration.CertificateChain is not null)
                        {
                            X509Certificate2Collection collection = X509CertificateLoader.LoadPkcs12CollectionFromFile(
                                new FileInfo(Path.Combine(configuration.ContentRootPath, configuration.ServerCertificate)).FullName,
                                configuration.ServerCertificatePassword
                            );

                            Dictionary<string, X509Certificate2> certificates = new Dictionary<string, X509Certificate2>();
                            foreach (X509Certificate2 certificate in collection.Where(cert => !cert.HasPrivateKey))
                            {
                                string name = certificate.GetNameInfo(X509NameType.SimpleName, false);
                                certificates.Add(name, certificate);
                            }

                            string[] certificateChain =
                                configuration.CertificateChain.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

                            httpsOptions.ServerCertificateChain = new X509Certificate2Collection();

                            foreach (string name in certificateChain)
                            {
                                if (certificates.TryGetValue(name, out X509Certificate2? certificate))
                                    httpsOptions.ServerCertificateChain.Add(certificate);
                            }
                        }

                        httpsOptions.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                        if (configuration.IncludeCipherSuites is not null)
                        {
                            string[] includeCipherSuites =
                            configuration.IncludeCipherSuites.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

                            if (includeCipherSuites.Length > 0)
                            {
                                httpsOptions.OnAuthenticate = (connectionContext, authenticationOptions) =>
                                {
                                    authenticationOptions.AllowRenegotiation = false;
                                    HashSet<TlsCipherSuite> tlsCipherSuites = new HashSet<TlsCipherSuite>();
                                    foreach (string cipherSuite in includeCipherSuites)
                                    {
                                        if (Enum.TryParse(cipherSuite, out TlsCipherSuite tlsCipherSuite))
                                            tlsCipherSuites.Add(tlsCipherSuite);
                                    }


                                    if (tlsCipherSuites.Count > 0)
                                        authenticationOptions.CipherSuitesPolicy = new CipherSuitesPolicy(tlsCipherSuites);
                                };
                            }
                        }
                    });
                });

                options.ConfigureEndpointDefaults(configureOptions =>
                {
                    configureOptions.Protocols = HttpProtocols.Http2 | HttpProtocols.Http3;
                });
            });

            builder.Services.AddSystemd();
            builder.Services.AddWindowsService();
            builder.Services.AddGrpc();
            builder.Services.AddSingleton(configuration);
            builder.Services.AddSingleton<ITaskScheduler, DefaultTaskScheduler>();
            builder.Services.AddSingleton<RecordExpirationTask>();

            ClientSecretCredential credential = new ClientSecretCredential(configuration.AzureDns.TenantId, configuration.AzureDns.ClientId, configuration.AzureDns.ClientSecret);
            ArmClient client = new ArmClient(credential);
            builder.Services.AddSingleton(client);

            builder.Services.AddHostedService<ServerHostedService>();

            return builder.Build();
        }

        private static async Task StartAsync(WebApplication app, CmdMain cmdMain, Configuration configuration)
        {
            app.MapGrpcService<DynamicDnsServer>();
            await app.RunAsync();
        }
    }
}
