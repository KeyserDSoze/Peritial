using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Expertiser.Database.Models
{
    [Table("Table_Contributor")]
    public partial class TableContributor
    {
        public TableContributor()
        {
            TableExpertise = new HashSet<TableExpertise>();
        }

        public int MemberId { get; set; }
        public int ContributorId { get; set; }
        [Key]
        public int Id { get; set; }
        public bool IsActive { get; set; }

        [ForeignKey(nameof(ContributorId))]
        [InverseProperty(nameof(TableMember.TableContributorContributor))]
        public virtual TableMember Contributor { get; set; }
        [ForeignKey(nameof(MemberId))]
        [InverseProperty(nameof(TableMember.TableContributorMember))]
        public virtual TableMember Member { get; set; }
        [InverseProperty("Contributor")]
        public virtual ICollection<TableExpertise> TableExpertise { get; set; }
    }
}
