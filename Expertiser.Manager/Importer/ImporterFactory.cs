using Expertiser.Database.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace Expertiser
{
    public class ImporterFactory
    {
        public IImporter Create(ImporterType type)
        {
            switch (type)
            {
                case ImporterType.Classic:
                    return new ClassicXml();
                case ImporterType.Accertatore:
                    return new AccertatoreXml();
                default:
                    throw new NotImplementedException();
            }
        }
    }
}
