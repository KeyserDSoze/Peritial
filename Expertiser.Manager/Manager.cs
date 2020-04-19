using Expertiser.Database.Models;
using System;

namespace Expertiser
{
    public class Manager
    {
        private ContextExpertiser contextExpertiser;
        public ContextExpertiser ContextExpertiser => contextExpertiser ??= new ContextExpertiser(this.ConnectionString);
        private string ConnectionString => Const.DatabaseConnectionString;
    }
}
