using Expertiser.Database.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace Expertiser
{
    public interface IImporter
    {
        ImportedExperty Import(Stream content, Manager manager);
    }
}