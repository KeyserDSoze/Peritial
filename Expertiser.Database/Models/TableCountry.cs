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
            TableLocation = new HashSet<TableLocation>();
        }

        [Key]
        public int Id { get; set; }
        [Required]
        [StringLength(50)]
        public string Label { get; set; }

        [InverseProperty("Country")]
        public virtual ICollection<TableLocation> TableLocation { get; set; }
    }
}
