using System.Configuration.Annotation;

namespace DDNSService.Client
{
    public sealed class Configuration
    {
        [Property(PropertyType.STRING, required: true)]
        public string ContentRootPath { get; set; } = null!;

        [Property(PropertyType.STRING, required: true)]
        public string Address { get; set; } = null!;

        [Property(PropertyType.STRING, required: true)]
        public string ClientCertificate { get; set; } = null!;

        [Property(PropertyType.STRING, required: true)]
        public string ClientCertificatePassword { get; set; } = null!;
    }
}
