using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Expertiser.Database.Models
{
    [Table("Table_Branch")]
    public partial class TableBranch
    {
        public TableBranch()
        {
            TableBranchType = new HashSet<TableBranchType>();
        }

        [Key]
        public int Id { get; set; }
        [Required]
        [StringLength(100)]
        public string Label { get; set; }

        [InverseProperty("Branch")]
        public virtual ICollection<TableBranchType> TableBranchType { get; set; }
    }
}
