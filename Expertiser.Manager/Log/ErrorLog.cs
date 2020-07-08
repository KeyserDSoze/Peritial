using Rystem;
using Rystem.NoSql;
using System;
using System.Collections.Generic;
using System.Text;

namespace Expertiser
{
    public class ErrorLog : TableStorage
    {
        public string Message { get; set; }
        public override ConfigurationBuilder GetConfigurationBuilder()
        {
            return new ConfigurationBuilder()
                .WithNoSql(Const.StorageConnectionString)
                .WithTableStorage(new TableStorageBuilder())
                .Build();
        }
    }
}
