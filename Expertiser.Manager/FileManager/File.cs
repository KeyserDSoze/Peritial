using Rystem;
using Rystem.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Rystem.Data.Integration;

namespace Expertiser
{
    public class File : IFileData
    {
        public Stream Stream { get; set; }
        public string Name { get; set; }
        public DataProperties Properties { get; set; }

        public ConfigurationBuilder GetConfigurationBuilder()
            => new ConfigurationBuilder().WithData(Const.StorageConnectionString)
                .WithBlockBlobStorage(new BlockBlobBuilder("files", null, new FileDataManager<File>()))
                .Build();
    }
}