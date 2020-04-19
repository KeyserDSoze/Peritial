using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Expertiser.Database.Models
{
    [Table("Table_Country")]
    public partial class TableCountry
    {
        public TableCountry()
        {
            TableCity = new HashSet<TableCity>();
        }

        [Key]
        public int Id { get; set; }
        [StringLength(3)]
        public string CurrencyCode { get; set; }
        [StringLength(2)]
        public string AlphaCode2 { get; set; }
        [StringLength(3)]
        public string AlphaCode3 { get; set; }
        public int? DialCode { get; set; }
        [Required]
        [StringLength(50)]
        public string Label { get; set; }

        [InverseProperty("Country")]
        public virtual ICollection<TableCity> TableCity { get; set; }
    }
}
