using System;
using System.IO;
using System.Xml;

namespace Expertiser
{
    public class XmlImportTypeResolver
    {
        public ImporterType Resolve(Stream content)
        {
            if (content == null)
                throw new ArgumentNullException(nameof(content));

            long originalPosition = 0;
            if (content.CanSeek)
            {
                originalPosition = content.Position;
                content.Position = 0;
            }

            try
            {
                using var reader = XmlReader.Create(content, new XmlReaderSettings()
                {
                    IgnoreComments = true,
                    IgnoreWhitespace = true,
                    DtdProcessing = DtdProcessing.Prohibit,
                });
                reader.MoveToContent();
                return reader.Name switch
                {
                    ClassicXml.RootName => ImporterType.Classic,
                    ClassicXml.BaseRootName => ImporterType.Classic,
                    AccertatoreXml.RootName => ImporterType.Accertatore,
                    _ => throw new InvalidDataException($"Tracciato XML non supportato: '{reader.Name}'."),
                };
            }
            catch (XmlException ex)
            {
                throw new InvalidDataException("Il file XML non e valido.", ex);
            }
            finally
            {
                if (content.CanSeek)
                    content.Position = originalPosition;
            }
        }
    }
}
