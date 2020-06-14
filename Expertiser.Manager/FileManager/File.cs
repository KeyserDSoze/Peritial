using Rystem;
using Rystem.Azure.Data;
using Rystem.Azure.Data.Integration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Expertiser
{
    public class File : IFileData
    {
        public Stream Stream { get; set; }
        public string Name { get; set; }
        public IDataProperties Properties { get; set; }

        public ConfigurationBuilder GetConfigurationBuilder()
            => new ConfigurationBuilder().WithData(Const.StorageConnectionString)
                .WithBlockBlobStorage(new BlockBlobBuilder("files", null, new FileDataManager<File>()))
                .Build();
    }
}