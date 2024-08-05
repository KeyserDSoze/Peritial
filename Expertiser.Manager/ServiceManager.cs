namespace Expertiser
{
    public class ServiceManager
    {
        private static readonly EmailSender emailSender = new EmailSender();
        public EmailSender EmailSender => emailSender;
    }
}