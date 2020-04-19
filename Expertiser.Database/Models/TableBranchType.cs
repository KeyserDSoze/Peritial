using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Expertiser.Database.Models
{
    [Table("Table_BranchType")]
    public partial class TableBranchType
    {
        public TableBranchType()
        {
            TableBranch = new HashSet<TableBranch>();
        }

        [Key]
        public int Id { get; set; }
        [Required]
        [StringLength(100)]
        public string Label { get; set; }

        [InverseProperty("BranchType")]
        public virtual ICollection<TableBranch> TableBranch { get; set; }
    }
}
