using System.Configuration.Annotation;

namespace DDNSService.Server
{
    public sealed class Configuration
    {
        [Property(PropertyType.STRING, required: true)]
        public string ContentRootPath { get; set; } = null!;

        [Property(PropertyType.USHORT, required: true)]
        public ushort? Port { get; set; }

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

        [Property(PropertyType.STRING, required: true)]
        public string ServerCertificate { get; set; } = null!;

        [Property(PropertyType.STRING, required: true)]
        public string ServerCertificatePassword { get; set; } = null!;

        [Property(PropertyType.STRING, required: false)]
        public string? CertificateChain { get; set; }

        [Property(PropertyType.STRING, required: false)]
        public string? IncludeCipherSuites { get; set; }
    }
}
