using Microsoft.AspNetCore.Identity.UI.Services;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;

namespace Expertiser
{
    public class EmailSender : IEmailSender
    {
        private readonly string Title = new Manager().Title;
        public async Task SendEmailAsync(string email, string subject, string htmlMessage)
        {
            try
            {
                var fromAddress = new MailAddress(Const.Email.Username, this.Title);
                var toAddress = new MailAddress(email);
                var smtp = new SmtpClient
                {
                    Host = "smtp.gmail.com ",
                    //Host = "smtp.mail.yahoo.com",
                    Port = 587,
                    EnableSsl = true,
                    //DeliveryMethod = SmtpDeliveryMethod.Network,
                    //UseDefaultCredentials = false,
                    Credentials = new NetworkCredential(fromAddress.Address, Const.Email.Password)
                };
                using (var message = new MailMessage(fromAddress, toAddress)
                {
                    Subject = subject,
                    Body = htmlMessage,
                    IsBodyHtml = true,
                })
                {
                    await smtp.SendMailAsync(message);
                }
                //return Task.CompletedTask;
            }
            catch
            {

            }
        }
    }
}
