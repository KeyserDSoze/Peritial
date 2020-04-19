using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Expertiser.Database.Models
{
    [Table("Table_Member")]
    public partial class TableMember
    {
        public TableMember()
        {
            TableContributorContributor = new HashSet<TableContributor>();
            TableContributorMember = new HashSet<TableContributor>();
            TableExpertise = new HashSet<TableExpertise>();
            TableExternalAccount = new HashSet<TableExternalAccount>();
        }

        [Key]
        public int Id { get; set; }
        [Required]
        [StringLength(50)]
        public string Name { get; set; }
        [StringLength(20)]
        public string Title { get; set; }
        public int Type { get; set; }
        public bool IsActive { get; set; }

        [InverseProperty(nameof(TableContributor.Contributor))]
        public virtual ICollection<TableContributor> TableContributorContributor { get; set; }
        [InverseProperty(nameof(TableContributor.Member))]
        public virtual ICollection<TableContributor> TableContributorMember { get; set; }
        [InverseProperty("Member")]
        public virtual ICollection<TableExpertise> TableExpertise { get; set; }
        [InverseProperty("Member")]
        public virtual ICollection<TableExternalAccount> TableExternalAccount { get; set; }
    }
}
