using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Expertiser.Base.Utility
{
    public class Request
    {
        public static List<string> Make(string uri)
           => MakeAsync(uri).ConfigureAwait(false).GetAwaiter().GetResult();
        public static async Task<List<string>> MakeAsync(string uri)
        {
            List<string> lines = new List<string>();
            HttpWebRequest httpWebRequest = (HttpWebRequest)WebRequest.Create(uri);
            httpWebRequest.Method = "get";
            using (HttpWebResponse httpWebResponse = (HttpWebResponse)(await httpWebRequest.GetResponseAsync()))
            {
                using (StreamReader streamReader = new StreamReader(httpWebResponse.GetResponseStream()))
                {
                    while (!streamReader.EndOfStream)
                        lines.Add(await streamReader.ReadLineAsync());
                }
            }
            return lines;
        }
    }
}
