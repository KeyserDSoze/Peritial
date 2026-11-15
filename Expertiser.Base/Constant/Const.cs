using System;
using System.Collections.Generic;
using System.Text;

namespace Expertiser
{
    public static class Const
    {
#if DEBUG
        public static string DatabaseConnectionString => System.Environment.GetEnvironmentVariable("EXPERTISER_DATABASE_CONNECTION_STRING") ?? string.Empty;
        public static string StorageConnectionString => System.Environment.GetEnvironmentVariable("EXPERTISER_STORAGE_CONNECTION_STRING") ?? string.Empty;
#else
        public static string DatabaseConnectionString => System.Environment.GetEnvironmentVariable("EXPERTISER_DATABASE_CONNECTION_STRING") ?? string.Empty;
        public static string StorageConnectionString => System.Environment.GetEnvironmentVariable("EXPERTISER_STORAGE_CONNECTION_STRING") ?? string.Empty;
#endif
        public static class Email
        {
            //public const string Username = "experty.platform@yahoo.com";
            //public const string Password = "ehcoxattefsfhyqt";
            public const string Username = "donotreplyexpertyservice@gmail.com";
            public const string Password = "widq abjs xzdo xgju";
        }
    }
}
