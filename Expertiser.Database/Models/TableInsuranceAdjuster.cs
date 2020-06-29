using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Expertiser.Database.Models
{
    [Table("Table_InsuranceAdjuster")]
    public partial class TableInsuranceAdjuster
    {
        public TableInsuranceAdjuster()
        {
            TableAccident = new HashSet<TableAccident>();
        }

        [Key]
        public int Id { get; set; }
        [Required]
        [StringLength(120)]
        public string Name { get; set; }

        [InverseProperty("InsuranceAdjuster")]
        public virtual ICollection<TableAccident> TableAccident { get; set; }
    }
}
