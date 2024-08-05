using Rystem;
using System;
using System.Threading.Tasks;

namespace Expertiser
{
    public class TelemetryManager
    {
        private readonly Manager Manager;
        public MyTelemetry MyTelemetry { get; } = Telemetry.CreateNew<MyTelemetry>();
        public TelemetryManager(Manager manager)
            => this.Manager = manager;
        public async Task SaveExceptionLog(Exception exception)
            => await new ErrorLog() { Message = exception.ToString() }.UpdateAsync().NoContext();
        public Task EndTelemetryAsync()
            => this.MyTelemetry.StopAsync();
    }
    public class MyTelemetry : Telemetry
    {
        public override ConfigurationBuilder GetConfigurationBuilder()
        {
            return new ConfigurationBuilder().WithTelemetry(Const.DatabaseConnectionString)
                .WithSql(new SqlTelemetryBuilder("Telemetry"))
                .Build();
        }
    }
}