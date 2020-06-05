using System;
using System.Collections.Generic;
using System.Text;

namespace Expertiser.Base.Constant
{
    public static class Roles
    {
        public const string Admin = "Admin";
        public const string Contributor = "Contributor";
        public const string AdminContributor = "Admin,Contributor";
        public const string All = "Admin,Contributor,Reader";
        public const string Reader = "Reader";
    }
}
