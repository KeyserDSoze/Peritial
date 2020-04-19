using Expertiser.Database.Models;
using System;

namespace Expertiser
{
    public class Manager
    {
        private ContextExpertiser contextExpertiser;
        public ContextExpertiser ContextExpertiser => contextExpertiser ??= new ContextExpertiser(this.ConnectionString);
        private string ConnectionString => "Server=tcp:kynsextesting.database.windows.net;Initial Catalog=Testing;Persist Security Info=True;User ID=kynsex;Password=396RfAqChDHXLvvR;MultipleActiveResultSets=True;Connection Timeout=30;";


    }
}
