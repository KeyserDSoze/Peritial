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
            TableExpertise = new HashSet<TableExpertise>();
        }

        [Key]
        public int Id { get; set; }
        [Required]
        [StringLength(100)]
        public string Label { get; set; }
        public int BranchId { get; set; }

        [ForeignKey(nameof(BranchId))]
        [InverseProperty(nameof(TableBranch.TableBranchType))]
        public virtual TableBranch Branch { get; set; }
        [InverseProperty("Branch")]
        public virtual ICollection<TableExpertise> TableExpertise { get; set; }
    }
}
