using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace Expertiser.Base.Utility
{
    public class Request
    {
        public static string Make(string uri)
           => MakeAsync(uri).ConfigureAwait(false).GetAwaiter().GetResult();
        public static async Task<string> MakeAsync(string uri)
        {
            HttpWebRequest httpWebRequest = (HttpWebRequest)WebRequest.Create(uri);
            httpWebRequest.Method = "get";
            using (HttpWebResponse httpWebResponse = (HttpWebResponse)(await httpWebRequest.GetResponseAsync()))
            {
                using (StreamReader streamReader = new StreamReader(httpWebResponse.GetResponseStream()))
                {
                    return await streamReader.ReadToEndAsync();
                }
            }
        }
    }
}
