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
            TableExpertise = new HashSet<TableExpertise>();
        }

        [Key]
        public int Id { get; set; }
        [Required]
        [StringLength(100)]
        public string Label { get; set; }
        public int BranchTypeId { get; set; }

        [ForeignKey(nameof(BranchTypeId))]
        [InverseProperty(nameof(TableBranchType.TableBranch))]
        public virtual TableBranchType BranchType { get; set; }
        [InverseProperty("Branch")]
        public virtual ICollection<TableExpertise> TableExpertise { get; set; }
    }
}
