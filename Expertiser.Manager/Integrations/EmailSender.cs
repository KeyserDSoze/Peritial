using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.Net.Mail;
using Microsoft.AspNetCore.Identity.UI.Services;

namespace Expertiser
{
    public class EmailSender : IEmailSender
    {
        private readonly string Title = new Manager().Title;
        public Task SendEmailAsync(string email, string subject, string htmlMessage)
        {
            //var fromAddress = new MailAddress(Const.Email.Username, this.Title);
            //var toAddress = new MailAddress(email);
            //var smtp = new SmtpClient
            //{
            //    Host = "smtp.mail.yahoo.com",
            //    Port = 587,
            //    EnableSsl = true,
            //    DeliveryMethod = SmtpDeliveryMethod.Network,
            //    UseDefaultCredentials = false,
            //    Credentials = new NetworkCredential(fromAddress.Address, Const.Email.Password)
            //};
            //using (var message = new MailMessage(fromAddress, toAddress)
            //{
            //    Subject = subject,
            //    Body = htmlMessage,
            //    IsBodyHtml = true,
            //})
            //{
            //    smtp.Send(message);
            //}
            return Task.CompletedTask;
        }
    }
}
