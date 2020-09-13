using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text;

namespace Expertiser.Database.Models
{
    public partial class AspNetUsers
    {
        [NotMapped]
        public string TotalName => $"{Name} {Surname}";
    }
}