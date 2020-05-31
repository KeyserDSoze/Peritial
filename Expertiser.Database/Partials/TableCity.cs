using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Expertiser.Database.Models
{
    public partial class TableCity
    {
        [NotMapped]
        public List<string> PostalCodes 
            => this.PostalCode.FromDefaultJson<List<string>>();
    }
}
