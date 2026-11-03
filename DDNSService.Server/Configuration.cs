using System.Configuration;
using System.Configuration.Annotation;

namespace DDNSService.Server
{
    public sealed class Configuration : IValidatableConfiguration
    {
        [Property(PropertyType.STRING, required: true)]
        public string ContentRootPath { get; set; } = null!;

        [Property(PropertyType.USHORT, required: true)]
        public ushort? Port { get; set; }

        public AzureDnsConfiguration AzureDns { get; set; } = null!;

        [Property(PropertyType.STRING, required: true)]
        public string ServerCertificate { get; set; } = null!;

        [Property(PropertyType.STRING, required: true)]
        public string ServerCertificatePassword { get; set; } = null!;

        [Property(PropertyType.STRING, required: false)]
        public string? CertificateChain { get; set; }

        [Property(PropertyType.STRING, required: false)]
        public string? IncludeCipherSuites { get; set; }

        public KmsConfiguration Kms { get; set; } = null!;

        public List<AllowedClientConfiguration> AllowedClients { get; set; } = null!;

        public void Validate()
        {
            if (AzureDns is null)
                throw new Exception("config field 'AzureDns' must be provided");
            ConfigurationValidator.Validate(AzureDns);

            if (Kms is null)
                throw new Exception("config field 'Kms' must be provided");
            ConfigurationValidator.Validate(Kms);

            if (AllowedClients is null)
                throw new Exception("config field 'AllowedClients' must be provided");
            foreach (AllowedClientConfiguration allowedClient in AllowedClients)
            {
                ConfigurationValidator.Validate(allowedClient);
                if (allowedClient.RecordNames is null || allowedClient.RecordNames.Count == 0)
                    throw new Exception($"config field 'AllowedClients[{allowedClient.Thumbprint}].RecordNames' must be provided");
            }
        }
    }

    public sealed class AllowedClientConfiguration
    {
        [Property(PropertyType.STRING, required: true)]
        public string Thumbprint { get; set; } = null!;

        public List<string> RecordNames { get; set; } = null!;
    }

    public sealed class AzureDnsConfiguration
    {
        [Property(PropertyType.STRING, required: true)]
        public string TenantId { get; set; } = null!;

        [Property(PropertyType.STRING, required: true)]
        public string ClientId { get; set; } = null!;

        [Property(PropertyType.STRING, required: true)]
        public string ClientSecret { get; set; } = null!;

        [Property(PropertyType.STRING, required: true)]
        public string ResourceId { get; set; } = null!;

        [Property(PropertyType.STRING, required: true)]
        public string ResourceGroupName { get; set; } = null!;

        [Property(PropertyType.STRING, required: true)]
        public string DnsZoneName { get; set; } = null!;
    }

    public sealed class KmsConfiguration
    {
        [Property(PropertyType.STRING, required: true)]
        public string DekFilePath { get; set; } = null!;

        [Property(PropertyType.STRING, required: false)]
        public string? KekSalt { get; set; }

        [Property(PropertyType.INT, required: false, DefaultValue = "600000")]
        public int? KekIterations { get; set; }

        [Property(PropertyType.STRING, required: false)]
        public string? CredentialStore { get; set; }
    }
}
