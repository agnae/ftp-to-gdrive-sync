namespace ftp_to_gdrive_sync.Types
{
    internal class ConfirmedFileUpload
    {
        public string FileName { get; set; }
        public string Year { get; set; }
        public string Month { get; set; }
        public string Day { get; set; }
        public string Hash { get; set; }
        public long FileSize { get; set; }
    }

    internal class AppSettings
    {
        public FtpSource[] FtpSources { get; set; }
        public GDrive GDrive { get; set; }
        public Slack Slack { get; set; }
        public Sentry Sentry { get; set; }
        public string ApplicationName { get; set; }
        public string DownloadPath { get; set; }
    }

    internal class FtpSource
    {
        public string Host { get; set; }
        public string[] Folders { get; set; }
    }

    internal class GDrive
    {
        public string ClientSecretsPath { get; set; }
        public string TokenDataStorePath { get; set; }
        public string RootFolder { get; set; }
    }

    internal class Slack
    {
        public string WebhookUrl { get; set; }
    }

    internal class Sentry
    {
        public string Dsn { get; set; }
    }
}