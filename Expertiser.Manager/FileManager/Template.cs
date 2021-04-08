using Rystem;
using Rystem.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Rystem.Data.Integration;

namespace Expertiser
{
    public class Template : IFileData
    {
        public Stream Stream { get; set; }
        public string Name { get; set; }
        public DataProperties Properties { get; set; }

        public ConfigurationBuilder GetConfigurationBuilder()
            => new ConfigurationBuilder().WithData(Const.StorageConnectionString)
                .WithBlockBlobStorage(new BlockBlobBuilder("templates", new FileDataManager<Template>(), new FileDataManager<Template>()))
                .Build();
    }
}