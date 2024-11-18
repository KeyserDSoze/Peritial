namespace Expertiser.UnitTest
{
    public class EmailTest
    {
        [Fact]
        public async Task SendAsync()
        {
            var emailSender = new EmailSender();
            await emailSender.SendEmailAsync("alessandro.rapiti44@gmail.com", "Test Number 131", "<div>Test Test Test</div>");
            Assert.True(true);
        }
    }
}
