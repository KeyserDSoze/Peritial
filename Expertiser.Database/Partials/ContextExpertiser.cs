using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Text;

namespace Expertiser.Database.Models
{
    public partial class ContextExpertiser
    {
        private string ConnectionString { get; }
        public ContextExpertiser(string connectionString)
        {
            this.ConnectionString = connectionString;
        }
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                optionsBuilder.UseSqlServer(this.ConnectionString);
            }
        }
    }
}
