using System;
using System.Collections.Generic;
using System.Text;

namespace Expertiser
{
    public static class Const
    {
#if DEBUG
        public const string DatabaseConnectionString = "Server=tcp:expertydb.database.windows.net;Initial Catalog=Experty;Persist Security Info=True;User ID=experty;Password=REDACTED;MultipleActiveResultSets=True;Connection Timeout=30;";
        public const string StorageConnectionString = "DefaultEndpointsProtocol=https;AccountName=expertysa;AccountKey=REDACTED;EndpointSuffix=core.windows.net";
#else
        public const string DatabaseConnectionString = "Server=tcp:expertydb.database.windows.net;Initial Catalog=Experty;Persist Security Info=True;User ID=experty;Password=REDACTED;MultipleActiveResultSets=True;Connection Timeout=30;";
        public const string StorageConnectionString = "DefaultEndpointsProtocol=https;AccountName=expertysa;AccountKey=REDACTED;EndpointSuffix=core.windows.net";
#endif
        public static class Email
        {
            public const string Username = "experty.platform@gmail.com";
            public const string Password = "ArwFn75jtHzk94P";
        }
    }
}
